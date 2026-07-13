# Tadka production incident injector (ADR-060 / Phase 9).
# Flips ONE real lever so students diagnose from dashboards/logs only.
#
#   pwsh scripts/inject-incident.ps1 -Scenario redis-down
#   pwsh scripts/inject-incident.ps1 -Scenario payment-slow
#   pwsh scripts/inject-incident.ps1 -Scenario kafka-down
#   pwsh scripts/inject-incident.ps1 -Scenario pool-tight
#   pwsh scripts/inject-incident.ps1 -Scenario load-shed
#   pwsh scripts/inject-incident.ps1 -Scenario backpressure
#   pwsh scripts/inject-incident.ps1 -Scenario random
#   pwsh scripts/inject-incident.ps1 -Scenario clear

param(
    [ValidateSet("redis-down","payment-slow","kafka-down","pool-tight","load-shed","backpressure","random","clear")]
    [string]$Scenario = "random"
)

$ErrorActionPreference = "Stop"
$scenarios = @("redis-down","payment-slow","kafka-down","pool-tight","load-shed","backpressure")
if ($Scenario -eq "random") {
    $Scenario = $scenarios | Get-Random
    Write-Host "RANDOM pick: $Scenario"
}

Write-Host "=== Injecting scenario: $Scenario ==="
Write-Host "Facilitator answer key: see docs/runbooks/incident-scenarios.md"

switch ($Scenario) {
    "clear" {
        docker compose start redis kafka 2>$null
        Write-Host "Started redis+kafka if stopped. Reset env levers on running processes manually (restart apps)."
        Write-Host "Clear LoadShed/Backpressure by unsetting env and restarting Tadka.Api."
    }
    "redis-down" {
        docker compose stop redis
        Write-Host "Redis stopped. Expect: menu cache miss path slower; SSE backplane degraded; rate limit may no-op if Redis-backed."
    }
    "payment-slow" {
        Write-Host "Set on Payment process and restart:"
        Write-Host "  Payment__Gateway__Behavior=Slow"
        Write-Host "  Payment__TimeoutSeconds=2"
        Write-Host "Expect: charge fails fast via timeout/bulkhead; circuit breaker may open under load (Day 14)."
    }
    "kafka-down" {
        docker compose stop kafka
        Write-Host "Kafka stopped. Expect: orders still 201; outbox rows accumulate; payment lag until kafka restarts."
    }
    "pool-tight" {
        Write-Host "Set on Monolith and restart:"
        Write-Host "  ConnectionStrings__TadkaDb=...;Minimum Pool Size=1;Maximum Pool Size=5"
        Write-Host "Then run k6 dinner-rush. Expect: pool exhaustion latency spike."
    }
    "load-shed" {
        Write-Host "Set on Monolith and restart:"
        Write-Host "  LoadShed__Enabled=true"
        Write-Host "Expect: /api/v1/orders/history and invoice 503; POST /orders still works."
    }
    "backpressure" {
        Write-Host "Set on Monolith and restart:"
        Write-Host "  Backpressure__MaxConcurrent=5"
        Write-Host "Then hammer with concurrent requests. Expect: 429 + Retry-After when over limit."
    }
}

Write-Host "=== Done. Do NOT tell students which scenario. ==="
