<#
.SYNOPSIS
  One-shot smoke for a cold cohort laptop: health -> login -> order -> (optional wait) status.

.DESCRIPTION
  Hits gateway if up (:8080), else Ordering (:5224). Does not require Kafka for a 201 on POST /orders.
  With full stack + Kafka, wait a few seconds and re-GET the order for Confirmed.

.EXAMPLE
  ./scripts/cohort-smoke.ps1
  ./scripts/cohort-smoke.ps1 -BaseUrl http://localhost:8080 -WaitSeconds 5
#>
param(
  [string]$BaseUrl = "",
  [int]$WaitSeconds = 0
)

$ErrorActionPreference = "Stop"

function Try-Url([string]$u) {
  try {
    $r = Invoke-WebRequest -Uri "$u/health" -UseBasicParsing -TimeoutSec 3
    return $r.StatusCode -eq 200
  } catch { return $false }
}

if (-not $BaseUrl) {
  if (Try-Url "http://localhost:8080") { $BaseUrl = "http://localhost:8080" }
  elseif (Try-Url "http://localhost:5224") { $BaseUrl = "http://localhost:5224" }
  else { Write-Error "Neither gateway :8080 nor Ordering :5224 answered /health. Start docker + apps first." }
}

Write-Host "== Cohort smoke against $BaseUrl ==" -ForegroundColor Cyan

$health = Invoke-RestMethod "$BaseUrl/health"
Write-Host "health: $($health.status)" -ForegroundColor Green

try {
  $ready = Invoke-RestMethod "$BaseUrl/health/ready"
  Write-Host "ready:  $($ready.status) (service=$($ready.service))" -ForegroundColor Green
} catch {
  Write-Host "ready:  (not available on this entry - ok for gateway-only)" -ForegroundColor Yellow
}

$login = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/auth/login" `
  -ContentType "application/json" `
  -Body '{"email":"priya@tadka.test","password":"Password123!"}'
$token = $login.accessToken
if (-not $token) { $token = $login.AccessToken }
if (-not $token) { Write-Error "Login did not return accessToken" }
Write-Host "login:  ok" -ForegroundColor Green

$headers = @{ Authorization = "Bearer $token"; "Idempotency-Key" = [guid]::NewGuid().ToString() }
$body = @{
  customerId = "c1b2c3d4-0001-4000-8000-000000000001"
  restaurantId = "a1b2c3d4-0001-4000-8000-000000000001"
  items = @(@{ menuItemId = "b1b2c3d4-0001-4000-8000-000000000001"; quantity = 1 })
  deliveryAddress = @{
    line1 = "smoke"; line2 = "lab"; city = "Bangalore"; pincode = "560066"
    latitude = 12.93; longitude = 77.61
  }
} | ConvertTo-Json -Depth 5

$order = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/v1/orders" -Headers $headers -ContentType "application/json" -Body $body
Write-Host "order:  $($order.id) status=$($order.status)" -ForegroundColor Green

if ($WaitSeconds -gt 0) {
  Write-Host "waiting ${WaitSeconds}s for saga..." -ForegroundColor Yellow
  Start-Sleep -Seconds $WaitSeconds
  $got = Invoke-RestMethod -Uri "$BaseUrl/api/v1/orders/$($order.id)" -Headers @{ Authorization = "Bearer $token" }
  Write-Host "order:  $($got.id) status=$($got.status)" -ForegroundColor Green
}

Write-Host "SMOKE OK" -ForegroundColor Cyan
