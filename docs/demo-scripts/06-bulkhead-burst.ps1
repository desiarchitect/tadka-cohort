# docs/demo-scripts/06-bulkhead-burst.ps1
# Day 7 — prove the bulkhead (ADR-021), not the timeout.
#
# The Fix-1 single curl (~2s) proves TIMEOUT. This script proves FAN-OUT:
#   10 parallel POSTs  → 10 payments Completed (~8s; they all fit in 10 slots)
#  100 parallel POSTs  → ~10 Completed, ~90 RateLimiterRejectedException (ms)
#
# The API must ALREADY be running with these env vars (Ctrl+C + restart; env is not reread):
#   $env:Payment__Mode = "Synchronous"
#   $env:Payment__Gateway__Behavior = "Slow"          # 8s hold — keeps the 10 slots occupied
#   $env:Payment__TimeoutSeconds = "30"               # MUST outlive the 8s delay or the 10 FAIL
#   $env:Payment__MaxConcurrentCharges = "10"
#
# Do NOT use TimeoutSeconds=2 here (those 10 would TimeoutRejected, zero Completed).
# Do NOT use Gateway=Fast (slots free in 200ms; you get more than 10 Completed).
#
# HTTP is still 201 for every POST — the order is created first. "Succeed" = payment Completed.
#
# Usage (from tadka repo root, PowerShell 5.1 or 7):
#   .\docs\demo-scripts\06-bulkhead-burst.ps1 -Count 10
#   .\docs\demo-scripts\06-bulkhead-burst.ps1 -Count 100
#
# Laptop honesty: expect ABOUT 10 Completed on the burst, not a perfect 10 (launch stagger).

param(
    [ValidateSet(10, 100)]
    [int]$Count = 10,
    [string]$BaseUrl = "http://localhost:5224"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$bodyFile = Join-Path $repoRoot "docs\runbooks\place-order.json"
if (-not (Test-Path $bodyFile)) { throw "Missing $bodyFile — run from the tadka repo (day-07)." }

$url = "$BaseUrl/api/v1/orders"
Write-Host "=============================================" -ForegroundColor Cyan
Write-Host " Bulkhead burst — $Count parallel POST /orders" -ForegroundColor Cyan
Write-Host "============================================="
Write-Host "Need: Synchronous + Slow + TimeoutSeconds=30 + MaxConcurrentCharges=10"
Write-Host "POST $url"
Write-Host ""

$runspacePool = [runspacefactory]::CreateRunspacePool(1, $Count)
$runspacePool.Open()
$jobs = @()
1..$Count | ForEach-Object {
    $ps = [powershell]::Create().AddScript({
        param($Url, $BodyFile)
        $out = curl.exe -s -o NUL -w "%{http_code} %{time_total}" -X POST $Url -H "Content-Type: application/json" --data-binary "@$BodyFile"
        $out
    }).AddArgument($url).AddArgument($bodyFile)
    $ps.RunspacePool = $runspacePool
    $jobs += [PSCustomObject]@{ Run = $ps; Handle = $ps.BeginInvoke() }
}

Write-Host "Waiting ($Count requests; the ten that got a slot take ~8s)..."
$lines = foreach ($j in $jobs) {
    $j.Run.EndInvoke($j.Handle)
    $j.Run.Dispose()
}
$runspacePool.Close()

$parsed = foreach ($line in $lines) {
    $parts = ($line | Out-String).Trim() -split "\s+"
    if ($parts.Count -ge 2) {
        [PSCustomObject]@{ Code = $parts[0]; Seconds = [double]$parts[1] }
    }
}

$n = $parsed.Count
$fast = @($parsed | Where-Object { $_.Seconds -lt 1 }).Count
$slow = @($parsed | Where-Object { $_.Seconds -ge 5 }).Count
$codes = $parsed | Group-Object Code | ForEach-Object { "$($_.Name)×$($_.Count)" }

Write-Host ""
Write-Host "--- HTTP (all should be 201; the order is created either way) ---" -ForegroundColor Cyan
Write-Host "responses: $n   codes: $($codes -join ', ')"
Write-Host "finished in < 1s (bulkhead reject): $fast"
Write-Host "finished in >= 5s (held a Slow slot): $slow"
Write-Host ""

Write-Host "--- payments in the last 45s ---" -ForegroundColor Cyan
$sql = @"
SELECT "Status",
       CASE
         WHEN "FailureReason" IS NULL THEN '(none)'
         WHEN "FailureReason" LIKE '%RateLimiterRejected%' THEN 'RateLimiterRejectedException'
         WHEN "FailureReason" LIKE '%TimeoutRejected%' THEN 'TimeoutRejectedException'
         ELSE left("FailureReason", 48)
       END AS reason,
       count(*) AS n
FROM payment.payments
WHERE "CreatedAt" > now() - interval '45 seconds'
GROUP BY 1, 2
ORDER BY n DESC;
"@
docker exec tadka-postgres psql -U tadka -d tadka -c $sql

Write-Host ""
if ($Count -eq 10) {
    Write-Host "Expect: ~10 Completed, 0 rejects. All ~8s." -ForegroundColor Green
} else {
    Write-Host "Expect: ~10 Completed (the Slow slots) + ~90 RateLimiterRejectedException (ms)." -ForegroundColor Green
    Write-Host "Shape, not a perfect 10 — launch stagger on a laptop." -ForegroundColor Yellow
}
