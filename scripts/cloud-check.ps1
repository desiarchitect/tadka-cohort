<#
.SYNOPSIS
  End-to-end check of a LIVE Azure session (ADR-064): is everything cloud-up deployed actually working?

.DESCRIPTION
  It places ONE real order through the public gateway (plus one idempotency replay), then completes that
  delivery as its rider so the rider goes back to the pool: the demo seeds only THREE riders, and an order
  that is never delivered keeps its rider busy, so repeated runs would otherwise leave new orders waiting
  for a rider. It leaves one Delivered order in the database and changes nothing in Azure. Prints PASS / FAIL / WARN / SKIP
  per check and exits 1 if any check FAILED (WARN and SKIP do not fail the run).

  What it proves, in order:
    1. Azure:      the resource group exists and every container app is Succeeded and Running
    2. Edge:       gateway /health and /health/ready, public reads (restaurants, menu)
    3. Security:   no token -> 401, per-service JWT (payment, no token -> 401), customer cannot run a
                   charge (403), another customer cannot read your order, payment or tracking (403)
    4. Saga:       login -> POST /orders (201, server-side price) -> Confirmed (Kafka + Payment) ->
                   rider assigned (Delivery) -> payment readable by the owner
    5. Idempotency: the same Idempotency-Key replays the same order instead of creating a second one
    6. Realtime:   the SSE stream answers text/event-stream
       Riders:     the rider picks up and delivers (PickedUp -> Delivered), which frees the rider
    7. Network:    an internal service is not reachable from the internet
    8. Front Door: only when the session has one (CDN x-cache, origin lock); SKIP with -NoFrontDoor sessions
    9. Telemetry:  WARN if services log failures reaching the OTEL collector (see the Application Insights
                   check below: this script cannot read Application Insights itself)
   10. Autoscale:  only with -Burst: fires a burst at the gateway and checks that it scaled out

.PARAMETER Burst        Also run the autoscaling test (about 4 minutes: sustained load for a minute, then waits for the scale-out).
.PARAMETER AutoscaleOnly Run ONLY the autoscaling test (no order is placed).
.PARAMETER SkipLogs     Skip the telemetry-error scan (it reads each app's recent log lines).
.PARAMETER GatewayUrl   Override the URL (default: terraform output gateway_url).
.EXAMPLE
  ./scripts/cloud-check.ps1
  ./scripts/cloud-check.ps1 -Burst
  ./scripts/cloud-check.ps1 -AutoscaleOnly
#>
param(
    [switch]$Burst,
    [switch]$AutoscaleOnly,
    [switch]$SkipLogs,
    [string]$GatewayUrl
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "cloud-common.ps1")

Assert-Tool "az" "Install the Azure CLI (winget install Microsoft.AzureCLI)"
Assert-AzureLogin

# ---- where is the session? -------------------------------------------------------------------------------
$rg = "rg-tadka-session"
$fd = ""
$fdId = ""
if (-not $GatewayUrl) {
    Assert-Tool "terraform" "Install Terraform >= 1.6 (winget install Hashicorp.Terraform)"
    $o = Get-TfOutputs
    $GatewayUrl = $o["gateway_url"]
    $rg = $o["resource_group"]
    $fd = $o["front_door_url"]
    $fdId = $o["front_door_id"]
}
$gw = $GatewayUrl.TrimEnd("/")
$hasFd = [bool]$fd
$origin = if ($fdId) { @{ "X-Azure-FDID" = [string]$fdId } } else { @{} }

$script:results = New-Object System.Collections.ArrayList
function Check([string]$Name, [bool]$Ok, [string]$Detail = "", [switch]$WarnOnly) {
    $state = if ($Ok) { "PASS" } elseif ($WarnOnly) { "WARN" } else { "FAIL" }
    $color = switch ($state) { "PASS" { "Green" } "WARN" { "Yellow" } default { "Red" } }
    Write-Host ("  {0,-5} {1,-46} {2}" -f $state, $Name, $Detail) -ForegroundColor $color
    [void]$script:results.Add([pscustomobject]@{ State = $state; Name = $Name })
}
function Skip([string]$Name, [string]$Why) {
    Write-Host ("  {0,-5} {1,-46} {2}" -f "SKIP", $Name, $Why) -ForegroundColor DarkYellow
    [void]$script:results.Add([pscustomobject]@{ State = "SKIP"; Name = $Name })
}
function Get-UserToken([string]$Email) {
    $r = Invoke-Http -Method POST -Url "$gw/api/v1/auth/login" -Body (@{ email = $Email; password = "Password123!" } | ConvertTo-Json)
    if ($r.Status -ne 200) { return $null }
    $j = $r.Body | ConvertFrom-Json
    if ($j.accessToken) { return $j.accessToken } else { return $j.AccessToken }
}

function Get-ReplicaCount([string]$App) {
    try { return @(az containerapp replica list -g $rg -n $App -o json | ConvertFrom-Json).Count } catch { return -1 }
}

function Show-Summary {
    $pass = @($script:results | Where-Object State -eq "PASS").Count
    $fail = @($script:results | Where-Object State -eq "FAIL").Count
    $warn = @($script:results | Where-Object State -eq "WARN").Count
    $skip = @($script:results | Where-Object State -eq "SKIP").Count
    Write-Host ("`n=== {0} PASS, {1} FAIL, {2} WARN, {3} SKIP in {4:N0} s ===" -f $pass, $fail, $warn, $skip, ((Get-Date) - $started).TotalSeconds) -ForegroundColor $(if ($fail) { "Red" } elseif ($warn) { "Yellow" } else { "Green" })
    if ($fail) { Write-Host "FAILED:" -ForegroundColor Red; $script:results | Where-Object State -eq "FAIL" | ForEach-Object { Write-Host "  - $($_.Name)" -ForegroundColor Red }; exit 1 }
    Write-Host "This session bills while it exists. Run ./scripts/cloud-down.ps1 when you are done." -ForegroundColor Yellow
    exit 0
}

# Autoscaling: the gateway has an HTTP-concurrency rule (30 concurrent requests per replica, 1..5). What
# matters is requests IN FLIGHT, not requests per second: the gateway answers in about 40 ms, so even a few
# hundred requests a second is only a handful in flight and never trips the rule. The round trip from a laptop
# is mostly network time (the server only counts the ~40 ms it works on a request), so it takes about a
# thousand virtual users to saturate one gateway replica; once it slows down, requests pile up in flight and
# the rule fires.
function Test-Autoscale {
    if (-not (Get-Command k6 -ErrorAction SilentlyContinue)) { Skip "autoscaling" "k6 is not installed (winget install k6)"; return }
    $before = Get-ReplicaCount "gateway"
    if ($before -gt 1) { Write-Host "  NOTE  gateway already has $before replicas (a recent burst). Scale-down takes about 5 minutes; wait, then re-run for a meaningful result." -ForegroundColor Yellow }
    $js = Join-Path ([System.IO.Path]::GetTempPath()) "tadka-autoscale.js"
    $out = Join-Path ([System.IO.Path]::GetTempPath()) "tadka-autoscale.out"
    Set-Content -Path $js -Encoding ascii -Value @"
import http from 'k6/http';
export const options = { vus: 1000, duration: '60s' };
export default function () { http.get(__ENV.GW + '/api/v1/restaurants'); }
"@
    Write-Host "  ...k6: 1000 virtual users against the gateway for 60 s" -ForegroundColor DarkGray
    $p = Start-Process k6 -ArgumentList @("run", "--no-color", "-e", "GW=$gw", "`"$js`"") -RedirectStandardOutput $out -RedirectStandardError "$out.err" -PassThru -WindowStyle Hidden
    $peak = $before
    while (-not $p.HasExited) { Start-Sleep -Seconds 10; $c = Get-ReplicaCount "gateway"; if ($c -gt $peak) { $peak = $c } }
    $summary = Get-Content $out -Raw -ErrorAction SilentlyContinue
    $failPct = if ($summary -match "http_req_failed[\.\s]*:\s*([0-9.]+)%") { [double]$Matches[1] } else { -1 }
    $reqs = if ($summary -match "http_reqs[\.\s]*:\s*(\d+)") { [int]$Matches[1] } else { 0 }
    Check "requests under load succeed" (($failPct -ge 0) -and ($failPct -lt 1)) "$reqs requests, $failPct% failed"
    # KEDA polls about every 30 s and a new replica takes a while to start: wait up to 2.5 more minutes.
    $deadline = (Get-Date).AddSeconds(150)
    while ($peak -le $before -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 15
        $c = Get-ReplicaCount "gateway"; if ($c -gt $peak) { $peak = $c }
    }
    # WARN, not FAIL: whether a laptop can put 30+ requests in flight on one replica varies run to run (it
    # scaled to 3 replicas once, and did not in four later runs). A missed scale-out says "the load was not
    # concurrent enough", not "the deployment is broken". For a reliable demo lower concurrentRequests
    # (deploy/azure/apps.tf) or run Day 16 k6 stress.js from a machine closer to the region.
    Check "gateway scaled out under load" ($peak -gt $before) "replicas $before -> $peak (rule: 30 concurrent requests per replica, max 5)" -WarnOnly
}
Write-Host "`n=== Tadka cloud-check: $gw ===" -ForegroundColor Cyan
$started = Get-Date

if ($AutoscaleOnly) {
    Write-Host "`n-- Autoscaling only --" -ForegroundColor Cyan
    Test-Autoscale
    Show-Summary
}

# ---- 1. Azure ---------------------------------------------------------------------------------------------
Write-Host "`n-- 1. Azure resources --" -ForegroundColor Cyan
$exists = (az group exists --name $rg).Trim()
Check "resource group $rg exists" ($exists -eq "true")
if ($exists -eq "true") {
    $apps = az containerapp list -g $rg --query "[].{name:name,state:properties.provisioningState,run:properties.runningStatus}" -o json | ConvertFrom-Json
    $expected = "gateway", "api", "payment", "delivery", "restaurant", "kafka", "redis", "otel-collector"
    foreach ($n in $expected) {
        $a = $apps | Where-Object { $_.name -eq $n }
        if (-not $a) { Check "container app $n" $false "not found"; continue }
        Check "container app $n" (($a.state -eq "Succeeded") -and ($a.run -eq "Running" -or -not $a.run)) "$($a.state) / $($a.run)"
    }
}

# ---- 2. Edge ----------------------------------------------------------------------------------------------
Write-Host "`n-- 2. Gateway and public reads --" -ForegroundColor Cyan
$r = Invoke-Http -Url "$gw/health";       Check "GET /health" ($r.Status -eq 200) "HTTP $($r.Status) $($r.Ms) ms"
$r = Invoke-Http -Url "$gw/health/ready"; Check "GET /health/ready" ($r.Status -eq 200) "HTTP $($r.Status)"
$r = Invoke-Http -Url "$gw/api/v1/restaurants" -Headers $origin
Check "GET /api/v1/restaurants" ($r.Status -eq 200) "HTTP $($r.Status) $($r.Ms) ms"
$r = Invoke-Http -Url "$gw/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu" -Headers $origin
Check "GET menu" ($r.Status -eq 200) "HTTP $($r.Status)"

# ---- 3. Security: before login ---------------------------------------------------------------------------
Write-Host "`n-- 3. Security (no token) --" -ForegroundColor Cyan
$r = Invoke-Http -Url "$gw/api/v1/orders/00000000-0000-0000-0000-000000000001" -Headers $origin
Check "GET order without a token -> 401" ($r.Status -eq 401) "HTTP $($r.Status)"
$r = Invoke-Http -Method POST -Url "$gw/api/v1/payments/charge" -Headers $origin -Body '{"orderId":"00000000-0000-0000-0000-000000000001","amount":1,"currency":"INR"}'
Check "payment charge without a token -> 401" ($r.Status -eq 401) "HTTP $($r.Status) (Payment validates the JWT itself)"

# ---- 4. The saga -------------------------------------------------------------------------------------------
Write-Host "`n-- 4. Order saga: order -> payment -> delivery --" -ForegroundColor Cyan
$priya = Get-UserToken "priya@tadka.test"
Check "login priya@tadka.test" ([bool]$priya) $(if ($priya) { "token ok" } else { "login failed" })
$rahul = Get-UserToken "rahul@tadka.test"
Check "login rahul@tadka.test" ([bool]$rahul) $(if ($rahul) { "token ok" } else { "login failed" })
if (-not $priya) {
    Write-Host "  Cannot continue the order checks without a login. Is the monolith (api) healthy?" -ForegroundColor Red
} else {
    $auth = @{ Authorization = "Bearer $priya" } + $origin
    $key = [guid]::NewGuid().ToString()
    $body = New-DemoOrderBody
    $post = Invoke-Http -Method POST -Url "$gw/api/v1/orders" -Headers ($auth + @{ "Idempotency-Key" = $key }) -Body $body
    $orderId = $null
    if ($post.Status -eq 201 -and $post.Body) { $orderId = ($post.Body | ConvertFrom-Json).id }
    Check "POST /orders -> 201" ($post.Status -eq 201) "HTTP $($post.Status) $($post.Ms) ms"
    if ($orderId) {
        $price = ($post.Body | ConvertFrom-Json).totalAmount.amount
        Check "server-side price" ($price -gt 0) "total $price (the client never sent a price)"

        $status = ""
        $ok = Wait-Until {
            $g = Invoke-Http -Url "$gw/api/v1/orders/$orderId" -Headers $auth
            if ($g.Status -eq 200) { $script:status = ($g.Body | ConvertFrom-Json).status }
            $script:status -eq "Confirmed"
        } -TimeoutSec 120 -EverySec 3 -What "order Confirmed"
        Check "order Confirmed (Kafka + Payment)" $ok "status=$status"

        $rider = ""
        $ok = Wait-Until {
            $t = Invoke-Http -Url "$gw/api/v1/deliveries/$orderId/track" -Headers $auth
            if ($t.Status -eq 200) { $script:rider = ($t.Body | ConvertFrom-Json).agentName }
            [bool]$script:rider
        } -TimeoutSec 90 -EverySec 3 -What "rider assignment"
        Check "rider assigned (Delivery)" $ok $(if ($ok) { "rider=$rider" } else { "no rider within 90 s. Only 3 riders are seeded and each undelivered order keeps one busy; deliver or wait for earlier orders to free them" })

        $p = Invoke-Http -Url "$gw/api/v1/payments/$orderId" -Headers $auth
        $pStatus = if ($p.Status -eq 200 -and $p.Body) { ($p.Body | ConvertFrom-Json).status } else { "" }
        Check "owner reads the payment" ($p.Status -eq 200) "HTTP $($p.Status) status=$pStatus"

        # ---- 5. idempotency ----
        Write-Host "`n-- 5. Idempotency --" -ForegroundColor Cyan
        $again = Invoke-Http -Method POST -Url "$gw/api/v1/orders" -Headers ($auth + @{ "Idempotency-Key" = $key }) -Body $body
        $sameId = if ($again.Body) { ($again.Body | ConvertFrom-Json).id -eq $orderId } else { $false }
        Check "same Idempotency-Key replays the order" (($again.Status -in 200, 201) -and $sameId) "HTTP $($again.Status), same order id: $sameId"

        # ---- ownership ----
        Write-Host "`n-- 6. Ownership and roles --" -ForegroundColor Cyan
        if ($rahul) {
            $rh = @{ Authorization = "Bearer $rahul" } + $origin
            $x = Invoke-Http -Url "$gw/api/v1/orders/$orderId" -Headers $rh;               Check "other customer reads the order -> 403" ($x.Status -eq 403) "HTTP $($x.Status)"
            $x = Invoke-Http -Url "$gw/api/v1/payments/$orderId" -Headers $rh;             Check "other customer reads the payment -> 403" ($x.Status -eq 403) "HTTP $($x.Status)"
            $x = Invoke-Http -Url "$gw/api/v1/deliveries/$orderId/track" -Headers $rh;     Check "other customer tracks the rider -> 403" ($x.Status -eq 403) "HTTP $($x.Status)"
        } else { Skip "other-customer checks" "rahul login failed" }
        $x = Invoke-Http -Method POST -Url "$gw/api/v1/payments/charge" -Headers $auth -Body (@{ orderId = $orderId; amount = 1; currency = "INR" } | ConvertTo-Json)
        Check "customer runs a charge -> 403 (Admin only)" ($x.Status -eq 403) "HTTP $($x.Status)"

        # ---- SSE ----
        Write-Host "`n-- 7. Realtime (SSE) --" -ForegroundColor Cyan
        $sse = & curl.exe -s -N --max-time 5 -D - -o NUL -H "Authorization: Bearer $priya" "$gw/api/v1/orders/$orderId/events" 2>$null
        $sseOk = ($sse -join "`n") -match "text/event-stream"
        Check "SSE /orders/{id}/events" $sseOk $(if ($sseOk) { "text/event-stream" } else { "no event-stream header" })

        # ---- rider lifecycle (also frees the rider for the next run) ----
        Write-Host "`n-- 8. Rider lifecycle --" -ForegroundColor Cyan
        if ($rider) {
            Start-Sleep -Seconds 3   # the credential endpoints allow 5 logins per 10 s per IP
            $riderToken = Get-UserToken "$($rider.ToLower()).rider@tadka.test"
            if ($riderToken) { Check "login as the rider" $true "$($rider.ToLower()).rider@tadka.test" }
            else { Skip "rider lifecycle" "no login for rider $rider (rider accounts are not seeded on this branch)" }
            if ($riderToken) {
                $rAuth = @{ Authorization = "Bearer $riderToken" } + $origin
                $a = Invoke-Http -Method PATCH -Url "$gw/api/v1/deliveries/$orderId/status" -Headers $rAuth -Body '{"status":"PickedUp"}'
                $b = Invoke-Http -Method PATCH -Url "$gw/api/v1/deliveries/$orderId/status" -Headers $rAuth -Body '{"status":"Delivered"}'
                Check "rider picks up, then delivers (rider freed)" (($a.Status -eq 204) -and ($b.Status -eq 204)) "PickedUp HTTP $($a.Status), Delivered HTTP $($b.Status)"
            }
        } else { Skip "rider lifecycle" "no rider was assigned" }
    }
}

# ---- 8. Network -------------------------------------------------------------------------------------------
Write-Host "`n-- 9. Network isolation --" -ForegroundColor Cyan
$internalFqdn = az containerapp show -g $rg -n payment --query "properties.configuration.ingress.fqdn" -o tsv 2>$null
if ($internalFqdn) {
    $d = Invoke-Http -Url "https://$internalFqdn/health" -TimeoutSec 10
    Check "internal service not reachable from the internet" ($d.Status -eq 0 -or $d.Status -ge 400) "payment internal address -> $(if ($d.Status -eq 0) { 'no route' } else { "HTTP $($d.Status)" })"
} else { Skip "internal service not reachable" "could not read the payment address" }

# ---- 10. Front Door -------------------------------------------------------------------------------------------
Write-Host "`n-- 10. Front Door --" -ForegroundColor Cyan
if ($hasFd) {
    $r1 = Invoke-Http -Url "$fd/api/v1/restaurants"; Start-Sleep -Seconds 2; $r2 = Invoke-Http -Url "$fd/api/v1/restaurants"
    $xc = Get-HeaderValue $r2.Headers "x-cache"
    Check "Front Door serves the API" (($r1.Status -eq 200) -and ($r2.Status -eq 200)) "HTTP $($r1.Status)/$($r2.Status)"
    Check "CDN cache hit on the repeat GET" ($xc -match "HIT") "x-cache=$xc" -WarnOnly
    $bypass = Invoke-Http -Url "$gw/api/v1/restaurants" -TimeoutSec 10
    Check "gateway URL without X-Azure-FDID -> 403" ($bypass.Status -eq 403) "HTTP $($bypass.Status)"
} else {
    Skip "CDN cache, WAF limit, origin lock" "this session has no Front Door (cloud-up -NoFrontDoor)"
}

# ---- 11. Telemetry ---------------------------------------------------------------------------------------
Write-Host "`n-- 11. Telemetry --" -ForegroundColor Cyan
if ($SkipLogs) { Skip "services reach the OTEL collector" "-SkipLogs" }
else {
    foreach ($app in "gateway", "api", "payment", "delivery", "restaurant") {
        # az logs can fail transiently (and writes to stderr); never let that abort the whole check.
        $lines = $null
        $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
        try { $lines = az containerapp logs show -g $rg -n $app --tail 200 --format text 2>$null } catch { $lines = $null }
        $logExit = $LASTEXITCODE
        $ErrorActionPreference = $prev
        if ($logExit -ne 0 -or -not $lines) { Skip "$app reaches the OTEL collector" "could not read its logs (az exit $logExit)"; continue }
        $bad = @($lines | Where-Object { $_ -match "otel-collector" -and $_ -match "Name or service not known|RequestFailed|RequestPipelineFailed" }).Count
        Check "$app reaches the OTEL collector" ($bad -eq 0) $(if ($bad -eq 0) { "no export failures in the last 200 log lines" } else { "$bad export failure line(s)" }) -WarnOnly
    }
    Write-Host "  NOTE  This cannot read Application Insights. Open it in the portal (Application map, Transaction search)." -ForegroundColor DarkGray
}

# ---- 12. Autoscale ---------------------------------------------------------------------------------------
Write-Host "`n-- 12. Autoscaling --" -ForegroundColor Cyan
if ($Burst) { Test-Autoscale }
else { Skip "autoscaling" "run with -Burst to test it (about 4 minutes), or -AutoscaleOnly for just this" }

Show-Summary