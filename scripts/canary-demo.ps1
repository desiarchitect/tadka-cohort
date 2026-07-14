# Restaurant canary rollout demo (ADR-061, Day-12 "buggy deploy" beat).
# Ramps traffic to a v2-with-a-bug Restaurant.Api instance behind the gateway's WeightedCanaryPolicy,
# 5% -> 25% -> 100%, so the room watches error rate rise with the weight, then rolls back to 0%.
#
# Prerequisite (two Restaurant.Api instances, matching the gateway's ReverseProxy clusters):
#   stable (:5260): dotnet run --project src/Tadka.Restaurant.Api --urls http://localhost:5260
#   canary (:5261): Restaurant__Buggy=true dotnet run --project src/Tadka.Restaurant.Api --urls http://localhost:5261
#
#   pwsh scripts/canary-demo.ps1 -Percent 5
#   pwsh scripts/canary-demo.ps1 -Percent 25
#   pwsh scripts/canary-demo.ps1 -Percent 100
#   pwsh scripts/canary-demo.ps1 -Percent 0     # rollback

param(
    [ValidateRange(0, 100)]
    [int]$Percent = 5
)

$ErrorActionPreference = "Stop"
$configPath = Join-Path $PSScriptRoot "..\src\Tadka.Gateway\appsettings.json"

# Surgical in-place replace, not a full parse+ConvertTo-Json round-trip: the latter reformats the
# ENTIRE file (PowerShell's JSON serializer doesn't preserve the original compact style), which would
# turn a one-value lever flip into a huge noisy diff every time a facilitator runs this live.
$raw = Get-Content $configPath -Raw
$updated = $raw -replace '"RestaurantPercent"\s*:\s*\d+', "`"RestaurantPercent`": $Percent"
if ($updated -eq $raw -and $raw -notmatch "`"RestaurantPercent`":\s*$Percent\b") {
    throw "Could not find `"RestaurantPercent`" in $configPath - has the gateway config shape changed?"
}
Set-Content -Path $configPath -Value $updated -NoNewline

Write-Host "=== Canary weight set: $Percent% of restaurant traffic -> :5261 (canary) ==="
if ($Percent -eq 0) {
    Write-Host "Rolled back. 100% of restaurant traffic now goes to :5260 (stable)."
} else {
    Write-Host "Gateway's appsettings.json reloads on change (JSON provider, reloadOnChange=true) - no restart needed."
    Write-Host "Canary lever (Restaurant:Buggy=true on the :5261 instance) fails GET /api/v1/restaurants with 500."
    Write-Host "Drive traffic through the gateway, e.g.:"
    Write-Host "  pwsh scripts/measure-load.ps1 -Url http://localhost:8080/api/v1/restaurants -Concurrency 20 -Total 200 -Label canary-$Percent"
    Write-Host "Watch error rate on the Grafana RED dashboard (Day 13) - it should track roughly the $Percent% weight."
    Write-Host "If error rate spikes, rollback: pwsh scripts/canary-demo.ps1 -Percent 0"
}
