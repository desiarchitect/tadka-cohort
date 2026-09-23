<#
.SYNOPSIS
  Day 14 cloud failover beat (ADR-064): run a request loop, trigger a real Postgres or Redis failover in the
  `ha` environment, and print the CAPTURED error count, error window and max latency. Numbers are measured
  here, never assumed.

.DESCRIPTION
  Requires `./scripts/cloud-up.ps1 -Mode ha`. The loop hits the GATEWAY URL directly, not Front Door, so
  the 30 s CDN cache on restaurant reads can't hide a Redis blip. The gateway URL is locked to Front Door
  (ADR-064 origin lockdown), so the loop sends the X-Azure-FDID header from `terraform output front_door_id`.

  -Target db    loop = POST /api/v1/orders (writes are what a DB failover breaks)
                trigger = az postgres flexible-server restart --failover Forced|Planned
  -Target redis loop = GET /api/v1/restaurants/{id}/menu (cache-aside read, ADR-018)
                forced  = deactivate the primary Redis node's revision for -OutageSeconds, then reactivate
                          (Sentinels see it down after 5 s and promote the replica)
                planned = SENTINEL FAILOVER via `az containerapp exec` (interactive; see the runbook)

  The Day-14 sequence: run -Target db -Kind forced with retry OFF (see the errors), then
  -SetRetry on (flips Database__EnableRetryOnFailure on the 4 services = new revisions), run it again.

.PARAMETER Target          db | redis
.PARAMETER Kind            forced | planned
.PARAMETER SetRetry        on | off: update Database__EnableRetryOnFailure on api/payment/delivery/restaurant first.
.PARAMETER DurationSeconds How long the loop runs (default 240; the failover starts ~10 s in).
.PARAMETER RedisPrimary    Which Redis node is primary right now (redis-a at start; redis-b after one failover).
.PARAMETER OutageSeconds   Redis forced: how long the primary stays down (default 30).
.EXAMPLE
  ./scripts/cloud-failover.ps1 -Target db -Kind forced
  ./scripts/cloud-failover.ps1 -Target db -Kind forced -SetRetry on
  ./scripts/cloud-failover.ps1 -Target redis -Kind forced
#>
param(
    [Parameter(Mandatory)][ValidateSet("db", "redis")][string]$Target,
    [ValidateSet("forced", "planned")][string]$Kind = "forced",
    [ValidateSet("on", "off")][string]$SetRetry,
    [int]$DurationSeconds = 240,
    [ValidateSet("redis-a", "redis-b")][string]$RedisPrimary = "redis-a",
    [int]$OutageSeconds = 30
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "cloud-common.ps1")

Assert-Tool "terraform" "Install Terraform >= 1.6"
Assert-Tool "az" "Install the Azure CLI"
Assert-AzureLogin

$o = Get-TfOutputs
if ($o["mode"] -ne "ha") { throw "Failover needs the ha environment (B1ms has no HA standby; basic Redis has no replica). Run cloud-up.ps1 -Mode ha." }
$rg = $o["resource_group"]
$gw = $o["gateway_url"]
$pg = $o["postgres_server"]
# The gateway URL is locked to Front Door (X-Azure-FDID, ADR-064). This loop must bypass the CDN, so it
# sends the header itself. A 403 here means the header is missing: fail loudly, never count it as success.
$origin = Get-OriginHeaders $o

# ── Optional: flip the retry flag (the "fix") ─────────────────────────────────────────────────────
if ($SetRetry) {
    $value = if ($SetRetry -eq "on") { "true" } else { "false" }
    Write-Host "Setting Database__EnableRetryOnFailure=$value on the 4 services (new revisions)..." -ForegroundColor Cyan
    foreach ($app in @("api", "payment", "delivery", "restaurant")) {
        az containerapp update -g $rg -n $app --set-env-vars "Database__EnableRetryOnFailure=$value" --output none
        if ($LASTEXITCODE -ne 0) { throw "az containerapp update failed for $app" }
    }
    Write-Host "Note: a later 'terraform apply' resets this unless you pass db_retry_enabled (cloud-up -DbRetry)." -ForegroundColor Yellow
    if (-not (Wait-Until { (Invoke-Http -Url "$gw/api/v1/restaurants" -Headers $origin).Status -eq 200 } -TimeoutSec 300 -What "new revisions healthy")) {
        throw "Services did not come back after the revision update."
    }
}

# ── Loop setup ────────────────────────────────────────────────────────────────────────────────────
$probe = Invoke-Http -Url "$gw/api/v1/restaurants" -Headers $origin
if ($probe.Status -eq 403) { throw "Gateway URL returned 403: origin lockdown rejected the X-Azure-FDID header. Check 'terraform output front_door_id'." }
$token = Get-DemoToken $gw $origin
$auth = @{ Authorization = "Bearer $token" } + $origin
$restaurantId = "a1b2c3d4-0001-4000-8000-000000000001"

$request = if ($Target -eq "db") {
    { Invoke-Http -Method POST -Url "$gw/api/v1/orders" -Headers ($auth + @{ "Idempotency-Key" = [guid]::NewGuid().ToString() }) -Body (New-DemoOrderBody) -TimeoutSec 30 }
} else {
    { Invoke-Http -Url "$gw/api/v1/restaurants/$restaurantId/menu" -Headers $origin -TimeoutSec 30 }
}

# ── Failover trigger (runs as a background job so the loop keeps measuring) ────────────────────────
$trigger = switch ("$Target/$Kind") {
    "db/forced"  { { param($rg, $pg) az postgres flexible-server restart -g $rg -n $pg --failover Forced --output none 2>&1 } }
    "db/planned" { { param($rg, $pg) az postgres flexible-server restart -g $rg -n $pg --failover Planned --output none 2>&1 } }
    "redis/forced" {
        { param($rg, $node, $outage)
            $rev = az containerapp revision list -g $rg -n $node --query "[?properties.active].name | [0]" -o tsv
            az containerapp revision deactivate -g $rg -n $node --revision $rev --output none 2>&1
            Start-Sleep -Seconds $outage
            az containerapp revision activate -g $rg -n $node --revision $rev --output none 2>&1
        }
    }
    "redis/planned" {
        { param($rg)
            # Interactive exec; if it can't attach in a job, run this line by hand in another terminal.
            az containerapp exec -g $rg -n sentinel-1 --command "redis-cli -p 26379 SENTINEL FAILOVER tadka" 2>&1
        }
    }
}
$jobArgs = switch ($Target) { "db" { @($rg, $pg) } "redis" { if ($Kind -eq "forced") { @($rg, $RedisPrimary, $OutageSeconds) } else { @($rg) } } }

Write-Host "=== Failover: target=$Target kind=$Kind, loop ${DurationSeconds}s against $gw ===" -ForegroundColor Cyan
$results = New-Object System.Collections.Generic.List[object]
$loopStart = Get-Date
$job = $null
$triggerAt = $null

while (((Get-Date) - $loopStart).TotalSeconds -lt $DurationSeconds) {
    if (-not $job -and ((Get-Date) - $loopStart).TotalSeconds -ge 10) {
        $triggerAt = Get-Date
        Write-Host ("[{0:HH:mm:ss}] >>> triggering {1} {2} failover" -f $triggerAt, $Target, $Kind) -ForegroundColor Yellow
        $job = Start-Job -ScriptBlock $trigger -ArgumentList $jobArgs
    }
    $r = & $request
    $now = Get-Date
    # 403 counts as an error too: it would mean the origin lock rejected us, and must never look like "no blip".
    $isError = ($r.Status -eq 0 -or $r.Status -ge 500 -or $r.Status -eq 403)
    $results.Add([pscustomobject]@{ At = $now; Status = $r.Status; Ms = $r.Ms; Error = $isError })
    if ($isError) { Write-Host ("[{0:HH:mm:ss}] ERROR  HTTP {1}  {2} ms" -f $now, $r.Status, $r.Ms) -ForegroundColor Red }
    elseif ($r.Ms -gt 1000) { Write-Host ("[{0:HH:mm:ss}] slow   HTTP {1}  {2} ms" -f $now, $r.Status, $r.Ms) -ForegroundColor DarkYellow }
    Start-Sleep -Milliseconds 250
}

$triggerDone = $null
if ($job) {
    Wait-Job $job -Timeout 900 | Out-Null
    $jobOut = Receive-Job $job
    $triggerDone = $job.PSEndTime
    Remove-Job $job -Force
    if ($jobOut) { Write-Host "trigger output: $($jobOut -join ' ')" -ForegroundColor DarkGray }
}

# ── Summary: captured, not invented ──────────────────────────────────────────────────────────────
$errors = @($results | Where-Object { $_.Error })
$ok = @($results | Where-Object { -not $_.Error })
$window = if ($errors.Count -gt 0) { ($errors[-1].At - $errors[0].At).TotalSeconds } else { 0 }
$sorted = @($results | Sort-Object Ms)
$p95 = if ($sorted.Count) { $sorted[[math]::Min($sorted.Count - 1, [math]::Floor($sorted.Count * 0.95))].Ms } else { 0 }
$max = if ($sorted.Count) { $sorted[-1].Ms } else { 0 }

$csv = Join-Path ([System.IO.Path]::GetTempPath()) ("tadka-failover-{0}-{1}-{2:yyyyMMdd-HHmmss}.csv" -f $Target, $Kind, $loopStart)
$results | Export-Csv -Path $csv -NoTypeInformation

Write-Host "`n=== RESULT ($Target / $Kind, retry=$(if ($SetRetry) { $SetRetry } else { 'unchanged' })) ===" -ForegroundColor Cyan
Write-Host ("  requests          : {0}" -f $results.Count)
Write-Host ("  errors (5xx/none) : {0}" -f $errors.Count)
Write-Host ("  error window      : {0:N1} s (first error -> last error)" -f $window)
Write-Host ("  max latency       : {0} ms   p95: {1} ms" -f $max, $p95)
if ($triggerAt -and $triggerDone) { Write-Host ("  failover command  : {0:N0} s" -f ($triggerDone - $triggerAt).TotalSeconds) }
Write-Host "  raw samples       : $csv"
Write-Host "Copy these into the Day-14 break-kit 'Cloud failover' table. Do not round them into prettier numbers." -ForegroundColor Yellow
if ($Target -eq "redis" -and $Kind -eq "forced") {
    Write-Host "Primary is now probably the OTHER node. Next run: -RedisPrimary $(if ($RedisPrimary -eq 'redis-a') { 'redis-b' } else { 'redis-a' })" -ForegroundColor Yellow
}
