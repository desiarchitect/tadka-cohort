<#
.SYNOPSIS
  Bring up the live Azure session environment (ADR-064): terraform apply -> wait for health -> smoke test
  through the Front Door URL -> print URLs + a cost reminder.

.DESCRIPTION
  One resource group, torn down after class with scripts/cloud-down.ps1. Images are pulled from GHCR
  (built by .github/workflows/images.yml); nothing is built here.

  Smoke (all through the PUBLIC Front Door URL):
    1. GET /api/v1/restaurants twice, showing the x-cache header (expect TCP_MISS then TCP_HIT)
    2. login priya@tadka.test
    3. POST /api/v1/orders -> 201
    4. poll until the order is Confirmed (proves Kafka + Payment)
    5. GET /api/v1/deliveries/{id}/track -> a rider is assigned (proves Delivery)
    6. SSE /api/v1/orders/{id}/events on the GATEWAY URL answers with text/event-stream (realtime does
       not go through the CDN on purpose: Front Door cuts long-lived responses, ADR-064)
    7. POST /api/v1/payments/charge with NO token -> 401 (per-service JWT, ADR-031)
    8. an internal service FQDN is NOT reachable from the internet (only the gateway is public)
    9. origin lockdown: the gateway URL without X-Azure-FDID -> 403; its /health stays 200 (probe exempt)

  Expected timings (see docs/runbooks/cloud-deploy.md): ~15-25 min basic, longer for ha (Postgres HA +
  replica). Front Door can return 404 for several minutes after apply while the route propagates.

.PARAMETER Mode        basic (Day 12, Day 16) | ha (Day 14 failover).
.PARAMETER AlertEmail  Budget alert recipient. Defaults to $env:TADKA_ALERT_EMAIL.
.PARAMETER ImageTag    GHCR tag to deploy (git sha or latest).
.PARAMETER DbRetry     Start with Database:EnableRetryOnFailure on. Day 14 starts WITHOUT it (see the failure).
.PARAMETER SkipSmoke   Apply + wait for health only.
.PARAMETER WithManagedRedis  ha mode only: also deploy Azure Managed Redis (HA on) next to Sentinel for a
                        side-by-side comparison. The apps keep using Sentinel. Adds hourly cost.
.PARAMETER LoadTest    Day 16: raise the Front Door WAF per-IP limit (3000/min default) to
                        waf_load_test_rate_limit_per_minute (60000/min) so one laptop's k6 run is not
                        blocked. Show the 403s WITHOUT it first. Re-run cloud-up with the SAME other
                        switches plus -LoadTest: the apply should change only the WAF policy (terraform
                        prints the plan; time it on the dry run).
.PARAMETER KafkaScaling  Optional: Payment scales 1..4 on order-placed consumer lag (KEDA) and auto-created
                        topics get 3 partitions. Off by default.
.PARAMETER NoFrontDoor  Skip Azure Front Door. Needed on a Free Trial or Student subscription, where Azure refuses it
                        (BadRequest: Free Trial and Student account is forbidden for Azure Frontdoor resources). The gateway
                        URL becomes the public entry point: no CDN cache, no WAF rate limit, no origin lock.
.PARAMETER AutoDownAfterHours  Backstop for a forgotten teardown: registers a one-time Windows scheduled task
                        (current user) that runs cloud-down.ps1 -Force this many hours from now. A
                        successful cloud-down removes the task. The budget alert lags 8-24 h; this doesn't.
.EXAMPLE
  ./scripts/cloud-up.ps1 -Mode basic -AlertEmail you@example.com -AutoDownAfterHours 7
  ./scripts/cloud-up.ps1 -Mode ha
  ./scripts/cloud-up.ps1 -Mode basic -LoadTest          # Day 16, after the 20 s burst has shown the 403s
#>
param(
    [ValidateSet("basic", "ha")]
    [string]$Mode = "basic",
    [string]$AlertEmail = $env:TADKA_ALERT_EMAIL,
    [string]$ImageTag = "latest",
    [switch]$DbRetry,
    [switch]$SkipSmoke,
    [switch]$WithManagedRedis,
    [switch]$LoadTest,
    [switch]$KafkaScaling,
    [switch]$NoFrontDoor,
    [ValidateRange(0.5, 24)]
    [double]$AutoDownAfterHours
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "cloud-common.ps1")

$started = Get-Date
Write-Host "=== Tadka cloud-up: mode=$Mode image=$ImageTag ===" -ForegroundColor Cyan

Assert-Tool "terraform" "Install Terraform >= 1.6 (winget install Hashicorp.Terraform)"
Assert-Tool "az" "Install the Azure CLI (winget install Microsoft.AzureCLI)"
Assert-AzureLogin
if (-not $AlertEmail) { throw "Pass -AlertEmail (or set TADKA_ALERT_EMAIL): the budget alert must reach a human." }
if ($WithManagedRedis -and $Mode -ne "ha") { throw "-WithManagedRedis only applies to -Mode ha (the Day 14 comparison)." }
if ($LoadTest -and $NoFrontDoor) { Write-Host "-LoadTest changes the Front Door WAF limit; with -NoFrontDoor there is no WAF, so it has no effect." -ForegroundColor Yellow }

# Container Apps, Front Door and friends need their resource providers registered once per subscription.
Ensure-ResourceProviders

# Session variables live in a gitignored tfvars file so cloud-down/cloud-failover reuse them.
@{
    mode                = $Mode
    image_tag           = $ImageTag
    db_retry_enabled     = [bool]$DbRetry
    enable_managed_redis = [bool]$WithManagedRedis
    load_test_mode       = [bool]$LoadTest
    kafka_consumer_scaling = [bool]$KafkaScaling
    enable_front_door    = -not $NoFrontDoor
    budget_alert_emails  = @($AlertEmail)
} | ConvertTo-Json | Set-Content -Path $script:VarsFile -Encoding ascii

# Register the backstop BEFORE apply: a half-finished apply still bills, so it needs the backstop too.
$autoDownAt = $null
if ($AutoDownAfterHours) {
    $autoDownAt = Register-AutoDownTask $AutoDownAfterHours
    Write-Host ("Backstop: scheduled task '{0}' runs cloud-down.ps1 at {1:yyyy-MM-dd HH:mm} (log: {2})." -f `
        $script:AutoDownTaskName, $autoDownAt, $script:AutoDownLog) -ForegroundColor Yellow
}

Write-Host "`n-- terraform init / apply (this is the slow part) --" -ForegroundColor Cyan
Invoke-Terraform @("init", "-input=false", "-upgrade")
Invoke-Terraform @("apply", "-input=false", "-auto-approve")
$applyDone = Get-Date

$o = Get-TfOutputs
$gw = $o["gateway_url"]
# No Front Door (-NoFrontDoor): the gateway is the public entry point and the origin lock is off.
$hasFd = [bool]$o["front_door_url"]
$fd = if ($hasFd) { $o["front_door_url"] } else { $gw }
$rg = $o["resource_group"]
# The gateway URL is locked to Front Door (ADR-064). Direct checks below send the header like Front Door does.
$origin = Get-OriginHeaders $o

# ── Wait for health ───────────────────────────────────────────────────────────────────────────────
Write-Host "`n-- waiting for the gateway, then Front Door --" -ForegroundColor Cyan
if (-not (Wait-Until { (Invoke-Http -Url "$gw/health").Status -eq 200 } -TimeoutSec 600 -What "gateway /health")) {
    throw "Gateway never became healthy. Check: az containerapp logs show -g $rg -n gateway"
}
if (-not (Wait-Until { (Invoke-Http -Url "$gw/api/v1/restaurants" -Headers $origin).Status -eq 200 } -TimeoutSec 600 -What "restaurants via gateway (services migrating)")) {
    throw "Restaurant service not answering via the gateway. Check: az containerapp logs show -g $rg -n restaurant"
}
if ($hasFd -and -not (Wait-Until { (Invoke-Http -Url "$fd/health").Status -eq 200 } -TimeoutSec 1500 -EverySec 20 -What "Front Door route propagation")) {
    throw "Front Door never answered 200 on /health. Propagation can be slow; re-run with -SkipSmoke later or check the portal."
}
$healthyAt = Get-Date

if (-not $SkipSmoke) {
    Write-Host "`n-- smoke test through $fd --" -ForegroundColor Cyan
    $fail = 0
    function Report([string]$Name, [bool]$Ok, [string]$Detail) {
        if ($Ok) { Write-Host ("  PASS  {0,-38} {1}" -f $Name, $Detail) -ForegroundColor Green }
        else { Write-Host ("  FAIL  {0,-38} {1}" -f $Name, $Detail) -ForegroundColor Red; $script:fail++ }
    }

    # 1. CDN: two GETs, watch x-cache.
    $r1 = Invoke-Http -Url "$fd/api/v1/restaurants"
    Start-Sleep -Seconds 2
    $r2 = Invoke-Http -Url "$fd/api/v1/restaurants"
    Report "GET restaurants #1" ($r1.Status -eq 200) "HTTP $($r1.Status) x-cache=$(Get-HeaderValue $r1.Headers 'x-cache') $($r1.Ms)ms"
    Report "GET restaurants #2 (expect a HIT)" ($r2.Status -eq 200) "HTTP $($r2.Status) x-cache=$(Get-HeaderValue $r2.Headers 'x-cache') $($r2.Ms)ms"

    # 2-3. login + order
    $token = Get-DemoToken $fd
    Report "login priya@tadka.test" $true "token ok"
    $auth = @{ Authorization = "Bearer $token" }
    $post = Invoke-Http -Method POST -Url "$fd/api/v1/orders" -Headers ($auth + @{ "Idempotency-Key" = [guid]::NewGuid().ToString() }) -Body (New-DemoOrderBody)
    Report "POST /orders" ($post.Status -eq 201) "HTTP $($post.Status) $($post.Ms)ms"
    $orderId = if ($post.Body) { ($post.Body | ConvertFrom-Json).id } else { $null }

    if ($orderId) {
        # 4. saga: Confirmed means order-placed -> Payment -> payment-results -> Ordering all worked.
        $status = ""
        $ok = Wait-Until {
            $g = Invoke-Http -Url "$fd/api/v1/orders/$orderId" -Headers $auth
            if ($g.Status -eq 200) { $script:status = ($g.Body | ConvertFrom-Json).status }
            $script:status -eq "Confirmed"
        } -TimeoutSec 120 -EverySec 3 -What "order Confirmed"
        Report "order Confirmed (Kafka + Payment)" $ok "status=$status"

        # 5. rider (Delivery consumed order-confirmed)
        $rider = ""
        $ok = Wait-Until {
            $t = Invoke-Http -Url "$fd/api/v1/deliveries/$orderId/track" -Headers $auth
            if ($t.Status -eq 200) { $script:rider = ($t.Body | ConvertFrom-Json).agentName }
            [bool]$script:rider
        } -TimeoutSec 90 -EverySec 3 -What "rider assignment"
        Report "rider assigned (Delivery)" $ok "rider=$rider"

        # 6. SSE on the GATEWAY URL, by design (ADR-064): Front Door is for cacheable request/response
        #    traffic and cuts long-lived responses at its origin timeout, so realtime uses the gateway's own
        #    hostname. The SSE path is exempt from origin lockdown; the JWT still guards it (ADR-031).
        #    Headers only (curl.exe ships with Windows 10+).
        $sseHeaders = & curl.exe -s -N --max-time 5 -D - -o NUL -H "Authorization: Bearer $token" "$gw/api/v1/orders/$orderId/events" 2>$null
        $sseOk = ($sseHeaders -join "`n") -match "text/event-stream"
        Report "SSE /orders/{id}/events (gateway URL)" $sseOk ($(if ($sseOk) { "text/event-stream" } else { "no event-stream header from $gw" }))
    }

    # 7. per-service JWT: Payment without a token.
    $pay = Invoke-Http -Method POST -Url "$fd/api/v1/payments/charge" -Body '{"orderId":"00000000-0000-0000-0000-000000000001","amount":1,"currency":"INR"}'
    Report "payment, no token -> 401" ($pay.Status -eq 401) "HTTP $($pay.Status)"

    # 8. internal-only: the payment app's internal FQDN must not answer from the internet.
    $internalFqdn = az containerapp show -g $rg -n payment --query "properties.configuration.ingress.fqdn" -o tsv 2>$null
    if ($internalFqdn) {
        $direct = Invoke-Http -Url "https://$internalFqdn/health" -TimeoutSec 10
        Report "internal service unreachable" ($direct.Status -eq 0 -or $direct.Status -ge 400) "https://$internalFqdn -> $(if ($direct.Status -eq 0) { 'no route (good)' } else { "HTTP $($direct.Status)" })"
    }

    # 9. origin lockdown: skipping Front Door is refused; the probe path is not. (Only exists with Front Door.)
    if ($hasFd) {
        $bypass = Invoke-Http -Url "$gw/api/v1/restaurants" -TimeoutSec 10
        Report "gateway URL, no X-Azure-FDID -> 403" ($bypass.Status -eq 403) "HTTP $($bypass.Status)"
    } else {
        Write-Host "  SKIP  origin lock, CDN hit and WAF checks      (-NoFrontDoor: no Front Door in this session)" -ForegroundColor DarkYellow
    }
    $probe = Invoke-Http -Url "$gw/health" -TimeoutSec 10
    Report "gateway /health exempt -> 200" ($probe.Status -eq 200) "HTTP $($probe.Status)"

    if ($fail -gt 0) { Write-Host "`nSMOKE: $fail check(s) failed." -ForegroundColor Red } else { Write-Host "`nSMOKE OK" -ForegroundColor Green }
}

$end = Get-Date
Write-Host "`n=== Tadka is live ($Mode) ===" -ForegroundColor Cyan
if ($hasFd) {
    Write-Host "  Front Door : $fd"
    Write-Host "  Gateway    : $gw  (realtime/SSE only; everything else 403s without Front Door)"
} else {
    Write-Host "  Gateway    : $gw  (public entry point; no Front Door: no CDN cache, WAF or origin lock)"
}
Write-Host "  Resource group: $rg"
if ($hasFd) { Write-Host "  WAF limit  : $($o["waf_rate_limit_per_minute"]) requests/min per client IP$(if ($LoadTest) { ' (-LoadTest)' })" }
if ($KafkaScaling) { Write-Host "  Kafka scaling: Payment 1..4 on order-placed lag, 3 partitions" }
if ($autoDownAt) { Write-Host ("  Auto-teardown backstop: {0:yyyy-MM-dd HH:mm} (task {1})" -f $autoDownAt, $script:AutoDownTaskName) }
if ($WithManagedRedis) { Write-Host "  Managed Redis (comparison only, apps use Sentinel): $($o["managed_redis"])" }
Write-Host ("  Timings    : apply {0:N1} min, healthy {1:N1} min, total {2:N1} min" -f `
    ($applyDone - $started).TotalMinutes, ($healthyAt - $started).TotalMinutes, ($end - $started).TotalMinutes)
Write-Host "`nCOST REMINDER: this bills every hour it exists. Run ./scripts/cloud-down.ps1 right after class." -ForegroundColor Yellow
Write-Host "  Estimates (NOT a bill): basic ~Rs 50-150/session, ha ~Rs 250-350 for 4 h. Record the real" -ForegroundColor Yellow
Write-Host "  Cost Management figure in docs/cost-model.md after the session." -ForegroundColor Yellow
