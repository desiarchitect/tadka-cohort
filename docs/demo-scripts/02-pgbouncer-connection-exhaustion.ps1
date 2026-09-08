# docs/demo-scripts/02-pgbouncer-connection-exhaustion.ps1
# Day 11 - the Day-5 promise comes due (ADR-015).
#
# Day 5 showed that ONE Tadka.Api instance can't truly exhaust a connection pool on a laptop.
# Day 11 is the first day with 2+ app instances, so this fires real concurrent load at BOTH
# instances at once and shows the difference between:
#   - both instances pointed straight at Postgres (each keeps its own Npgsql pool)
#   - both instances pointed at PgBouncer :6432 (Npgsql pools multiplexed onto ~20 backends)
#
# Usage:
#   .\02-pgbouncer-connection-exhaustion.ps1 -Urls "http://localhost:5224","http://localhost:5225" -Label "DIRECT (:5432)"
#   .\02-pgbouncer-connection-exhaustion.ps1 -Urls "http://localhost:5224","http://localhost:5225" -Label "VIA PGBOUNCER (:6432)"
#
# Which physical port each instance's Npgsql pool actually points at is set at instance
# startup (ConnectionStrings__TadkaDb env var) - this script only generates the load.

param(
    [string[]]$Urls = @("http://localhost:5224", "http://localhost:5225"),
    [string]$Label = "RUN",
    [int]$RequestsPerInstance = 150
)

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host " PgBouncer Connection Exhaustion Demo - $Label" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

$totalRequests = $Urls.Count * $RequestsPerInstance
$instanceCount = $Urls.Count
Write-Host "Firing $totalRequests concurrent requests across $instanceCount instances, $RequestsPerInstance per instance..." -ForegroundColor Yellow

$runspacePool = [runspacefactory]::CreateRunspacePool(1, $totalRequests)
$runspacePool.Open()
$jobs = @()
$sw = [System.Diagnostics.Stopwatch]::StartNew()

foreach ($baseUrl in $Urls) {
    $url = "$baseUrl/api/v1/restaurants"
    for ($i = 0; $i -lt $RequestsPerInstance; $i++) {
        $powershell = [powershell]::Create().AddScript({
            param($url)
            try {
                $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
                $response = Invoke-RestMethod -Uri $url -Method Get -ErrorAction Stop -TimeoutSec 20
                $sw2.Stop()
                return [PSCustomObject]@{ Ok = $true; Ms = $sw2.ElapsedMilliseconds; Error = $null }
            } catch {
                return [PSCustomObject]@{ Ok = $false; Ms = -1; Error = $_.Exception.Message }
            }
        }).AddArgument($url)

        $powershell.RunspacePool = $runspacePool
        $jobs += [PSCustomObject]@{ Run = $powershell; Handle = $powershell.BeginInvoke() }
    }
}

$results = @()
foreach ($job in $jobs) {
    $results += $job.Run.EndInvoke($job.Handle)
    $job.Run.Dispose()
}
$runspacePool.Close()
$sw.Stop()

$success = $results | Where-Object { $_.Ok }
$failed = $results | Where-Object { -not $_.Ok }
$p99 = if ($success.Count -gt 0) { ($success.Ms | Sort-Object)[[math]::Floor($success.Count * 0.99) - 1] } else { -1 }

Write-Host "`n--- RESULTS: $Label ---" -ForegroundColor Cyan
Write-Host "Total requests      : $totalRequests"
Write-Host "Wall clock          : $($sw.ElapsedMilliseconds) ms"
Write-Host "Successful          : $($success.Count)" -ForegroundColor Green
Write-Host "Failed              : $($failed.Count)" -ForegroundColor $(if ($failed.Count -gt 0) { "Red" } else { "Green" })
if ($success.Count -gt 0) {
    Write-Host "p99 latency (ok reqs): $p99 ms"
}
if ($failed.Count -gt 0) {
    Write-Host "`nSample failures:" -ForegroundColor Red
    $failed | Select-Object -First 3 -ExpandProperty Error | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
}
