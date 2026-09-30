<#
.SYNOPSIS
  Day 12 / ADR-050 schema-evolution break, applied to menu-updated: prove that renaming a required
  field is silent poison here too — and, because MenuItemReplica.PriceAmount has no DB column default
  (unlike Payment's Currency), the failure is a free order rather than a merely-wrong currency.

.DESCRIPTION
  Sibling to scripts/evolution-break.ps1 (which does this for Payment's order-placed). Wraps
  inject-poison-menu.ps1 -Mode BreakingRename and prints the verification SQL operators run.
  Bonus/break-kit material (like ADR-045/046) — not part of Day 12's tight class-time script.

.EXAMPLE
  ./scripts/evolution-break-menu.ps1
#>
$ErrorActionPreference = "Stop"

Write-Host "== Schema evolution break (ADR-050), menu-updated: rename PriceAmount -> Price ==" -ForegroundColor Cyan
Write-Host "This is NOT a crash demo. The danger is silence - and this time the silent value is the price." -ForegroundColor Yellow
Write-Host ""

& "$PSScriptRoot/inject-poison-menu.ps1" -Mode BreakingRename

Write-Host ""
Write-Host "Verify (after MenuUpdatedConsumer processes the message):" -ForegroundColor Cyan
Write-Host @'
  # Chicken Biryani's replica price is now 0.00 - no exception path fired
  docker exec tadka-postgres psql -U tadka -d tadka -c \
    "SELECT \"Name\", \"PriceAmount\" FROM ordering.menu_replica WHERE \"MenuItemId\"='b1b2c3d4-0001-4000-8000-000000000001';"

  # Consumer group lag is 0 - the bad message was "successfully" consumed
  docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh \
    --bootstrap-server localhost:9092 --command-config /etc/kafka/docker/client.properties --describe --group tadka-monolith-menu

  # Place an order for it and watch the total (LocalReplica mode reads straight from the row above)
  # POST /api/v1/orders with menuItemId b1b2c3d4-0001-4000-8000-000000000001 -> line total Rs 0.00
'@

Write-Host ""
Write-Host "Contrast: additive evolution is safe" -ForegroundColor Green
Write-Host "  ./scripts/inject-poison-menu.ps1 -Mode SafeExtraField"
Write-Host "  -> replica still prices Chicken Biryani at Rs 299.00; the unknown SpiceLevel field is ignored."
Write-Host ""
Write-Host "Fix discipline: additive-only schema evolution (ADR-050), same rule as Payment's order-placed." -ForegroundColor Cyan
Write-Host "Restore the row after the demo:" -ForegroundColor Cyan
Write-Host @'
  docker exec tadka-postgres psql -U tadka -d tadka -c \
    "UPDATE ordering.menu_replica SET \"PriceAmount\"=299.00 WHERE \"MenuItemId\"='b1b2c3d4-0001-4000-8000-000000000001';"
'@
