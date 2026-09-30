<#
.SYNOPSIS
  Day 9 / ADR-050 schema-evolution break: prove that renaming a required field is silent poison.

.DESCRIPTION
  Wraps inject-poison.ps1 MissingRequiredField and prints the verification SQL operators run.
  The sharp lesson: System.Text.Json does NOT throw on a missing constructor param — it binds null,
  the payment row gets Currency defaulted by the DB column default (INR), and zero errors appear
  in logs or lag. Renames are invisible production bugs.

.EXAMPLE
  ./scripts/evolution-break.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }

Write-Host "== Schema evolution break (ADR-050): rename Currency -> CurrencyCode ==" -ForegroundColor Cyan
Write-Host "This is NOT a crash demo. The danger is silence." -ForegroundColor Yellow
Write-Host ""

& "$PSScriptRoot/inject-poison.ps1" -Mode MissingRequiredField

Write-Host ""
Write-Host "Verify (after Payment consumer processes the message):" -ForegroundColor Cyan
Write-Host @'
  # Payment status is still Completed — no exception path fired
  docker exec tadka-payment-db psql -U tadka -d tadka_payment -c \
    "SELECT \"OrderId\", \"Status\", \"Amount\", \"Currency\" FROM payment.payments ORDER BY \"CreatedAt\" DESC LIMIT 5;"

  # Consumer group lag is 0 — the bad message was "successfully" consumed
  docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh \
    --bootstrap-server localhost:9092 --command-config /etc/kafka/docker/client.properties --describe --group tadka-payment
'@

Write-Host ""
Write-Host "Contrast: additive evolution is safe" -ForegroundColor Green
Write-Host "  ./scripts/inject-poison.ps1 -Mode SafeExtraField"
Write-Host "  → Payment still completes; unknown fields are ignored."
Write-Host ""
Write-Host "Fix discipline: additive-only schema evolution (ADR-050). Never rename/remove without dual-read dual-write."
