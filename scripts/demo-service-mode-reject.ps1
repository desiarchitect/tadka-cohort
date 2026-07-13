<#
.SYNOPSIS
  Live demo hints for ADR-062 DecisionMode=Service reject path.

.DESCRIPTION
  Does not restart processes (Windows multi-app). Prints exact env + verification SQL/curl.
  Automated equivalent: dotnet test --filter FullyQualifiedName~ServiceMode
#>
$ErrorActionPreference = "Stop"

Write-Host "== ADR-062 Service-mode restaurant reject ==" -ForegroundColor Cyan
Write-Host ""
Write-Host "1) Terminal Restaurant.Api:"
Write-Host '   $env:Restaurant__AcceptMode = "Reject"'
Write-Host "   dotnet run --project src/Tadka.Restaurant.Api"
Write-Host ""
Write-Host "2) Terminal Ordering:"
Write-Host '   $env:Restaurant__DecisionMode = "Service"'
Write-Host '   $env:Restaurant__RefundOnReject = "true"'
Write-Host "   dotnet run --project src/Tadka.Api"
Write-Host ""
Write-Host "3) Also run Payment + Delivery + docker compose (Kafka)."
Write-Host ""
Write-Host "4) Smoke:"
Write-Host "   powershell -File scripts/cohort-smoke.ps1 -WaitSeconds 8"
Write-Host "   Expect order Cancelled (not stuck Confirmed) and payment Refunded when refund path settles."
Write-Host ""
Write-Host "5) Verify:"
Write-Host '   docker exec tadka-restaurant-db psql -U tadka -d tadka_restaurant -c "SELECT * FROM restaurant.order_decisions ORDER BY \"DecidedAt\" DESC LIMIT 3;"'
Write-Host '   docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \"Topic\",\"Key\" FROM ordering.outbox_messages WHERE \"Topic\" IN (''refund-requested'') ORDER BY \"CreatedAt\" DESC LIMIT 5;"'
Write-Host ""
Write-Host "Stuck Confirmed? Restaurant.Api or Kafka down - see docs/runbooks/decision-mode-matrix.md"
Write-Host ""
Write-Host "Tests (no live stack):"
Write-Host "  dotnet test tests/Tadka.Api.Tests --filter FullyQualifiedName~ServiceMode"
