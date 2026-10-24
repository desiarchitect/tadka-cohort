# Runs one k6 profile against the Tadka gateway after a quick health check.
# Works in Windows PowerShell 5.1 and PowerShell 7. Run it from the repository root.
#   powershell -File run-k6.ps1 -Profile smoke
#   powershell -File run-k6.ps1 -Profile average-load -OrderToken $env:TOKEN -OrderCustomerId c1b2c3d4-0001-4000-8000-000000000001
[CmdletBinding()]
param(
    [ValidateSet('smoke', 'average-load', 'stress', 'spike', 'hot-key')]
    [string]$Profile = 'smoke',

    [string]$BaseUrl = 'http://localhost:8080',

    # Needed to exercise order writes. Without them only the browse reads run.
    [string]$OrderToken = '',
    [string]$OrderCustomerId = ''
)

$script = "k6/$Profile.js"
if (-not (Test-Path $script)) { throw "$script not found. Run this from the repository root." }
if (-not (Get-Command k6 -ErrorAction SilentlyContinue)) { throw "k6 is not on PATH. Install it with: winget install GrafanaLabs.k6" }

# Fail early, with a useful message, if the stack is not up.
try {
    $r = Invoke-WebRequest -Uri "$BaseUrl/api/v1/restaurants?page=1&pageSize=1" -UseBasicParsing -TimeoutSec 10
    if ($r.StatusCode -ne 200) { throw "status $($r.StatusCode)" }
} catch {
    throw "Gateway at $BaseUrl did not answer a restaurant list ($($_.Exception.Message)). Start the five services first (scripts/dev-cluster.ps1)."
}

Write-Host "Reminder: start the gateway with Gateway__RateLimitPerMinute=100000 and Tadka.Api with RateLimit__PerMinute=100000, or k6 will measure the rate limiter." -ForegroundColor Yellow
if (-not $OrderToken) { Write-Host "No -OrderToken: only browse reads will run (orders_placed stays 0)." -ForegroundColor Yellow }

# k6 reads BASE_URL, ORDER_TOKEN and ORDER_CUSTOMER_ID (see k6/lib.js).
$k6Args = @('run', '-e', "BASE_URL=$BaseUrl")
if ($OrderToken)      { $k6Args += @('-e', "ORDER_TOKEN=$OrderToken") }
if ($OrderCustomerId) { $k6Args += @('-e', "ORDER_CUSTOMER_ID=$OrderCustomerId") }
$k6Args += $script

Write-Host "k6 $($k6Args -join ' ')" -ForegroundColor Gray
& k6 @k6Args
exit $LASTEXITCODE
