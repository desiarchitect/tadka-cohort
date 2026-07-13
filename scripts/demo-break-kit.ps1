<#
.SYNOPSIS
  Instructor walkthrough: break -> observe -> fix for core cohort demos.

.DESCRIPTION
  Guided steps (pauses between scenarios). Assumes:
    docker compose up -d
    4 apps running (Payment, Delivery, Restaurant, Ordering) with Kafka on
  Uses Windows PowerShell 5-safe ASCII only.

.PARAMETER Scenario
  all | smoke | reject-inline | poison | kafka-down | evolution | service-mode-hint

.EXAMPLE
  powershell -File scripts/demo-break-kit.ps1
  powershell -File scripts/demo-break-kit.ps1 -Scenario poison
#>
param(
  [ValidateSet("all","smoke","reject-inline","poison","kafka-down","evolution","service-mode-hint")]
  [string]$Scenario = "all",
  [switch]$SkipPause
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }

function Pause-Step([string]$msg) {
  Write-Host ""
  Write-Host ">> $msg" -ForegroundColor Yellow
  if (-not $SkipPause) {
    Write-Host "   Press Enter to continue..." -ForegroundColor DarkGray
    [void][Console]::ReadLine()
  }
}

function Run-Smoke {
  Write-Host "`n=== 1) SMOKE (happy path) ===" -ForegroundColor Cyan
  Write-Host "Expect: health Ready, login ok, order Created then Confirmed (Kafka saga)."
  & "$PSScriptRoot/cohort-smoke.ps1" -WaitSeconds 5
}

function Run-RejectInline {
  Write-Host "`n=== 2) REJECT + REFUND (Inline, ADR-045) ===" -ForegroundColor Cyan
  Write-Host "BREAK: Restaurant rejects after payment settles."
  Write-Host "  Restart Ordering with:"
  Write-Host "    `$env:Restaurant__DecisionMode = 'Inline'"
  Write-Host "    `$env:Restaurant__AcceptMode = 'Reject'"
  Write-Host "    `$env:Restaurant__RefundOnReject = 'true'"
  Write-Host "FIX lever OFF (money stuck):"
  Write-Host "    `$env:Restaurant__RefundOnReject = 'false'"
  Write-Host "Automated proof (no restart needed):"
  Write-Host "  dotnet test tests/Tadka.Api.Tests --filter FullyQualifiedName~RestaurantReject"
  Pause-Step "Run the test or restart Ordering with AcceptMode=Reject, place an order, then press Enter"
}

function Run-Poison {
  Write-Host "`n=== 3) POISON MESSAGE + DLQ (ADR-051) ===" -ForegroundColor Cyan
  Write-Host "Injecting malformed JSON onto order-placed..."
  & "$PSScriptRoot/inject-poison.ps1" -Mode Malformed
  Write-Host "Watch Payment logs for retry then DLQ; lag unblocks."
  Write-Host "Replay: powershell -File scripts/replay-dlq.ps1"
  Pause-Step "Confirm DLQ routing in logs / Kafka UI"
}

function Run-KafkaDown {
  Write-Host "`n=== 4) KAFKA DOWN (ADR-027 catch-up) ===" -ForegroundColor Cyan
  & "$PSScriptRoot/inject-incident.ps1" -Scenario kafka-down
  Write-Host "Place an order now -> 201 Created, stays pending/Created until Kafka returns."
  Write-Host "Outbox rows accumulate on Ordering."
  Pause-Step "Place an order while Kafka is down, observe stuck status, then press Enter to restore"
  & "$PSScriptRoot/inject-incident.ps1" -Scenario clear
  Write-Host "After clear: wait ~5s, re-GET order -> Confirmed (catch-up)."
  Pause-Step "Verify order Confirmed after Kafka restart"
}

function Run-Evolution {
  Write-Host "`n=== 5) SCHEMA EVOLUTION SILENCE (ADR-050) ===" -ForegroundColor Cyan
  & "$PSScriptRoot/evolution-break.ps1"
  Pause-Step "Contrast with SafeExtraField (additive is fine)"
  & "$PSScriptRoot/inject-poison.ps1" -Mode SafeExtraField
}

function Run-ServiceModeHint {
  Write-Host "`n=== 6) SERVICE MODE ACCEPT/REJECT (ADR-062, Day 12+) ===" -ForegroundColor Cyan
  Write-Host "See docs/runbooks/decision-mode-matrix.md"
  Write-Host "  Ordering:  `$env:Restaurant__DecisionMode = 'Service'"
  Write-Host "  Restaurant:`$env:Restaurant__AcceptMode = 'Reject'"
  Write-Host "Place order -> Confirmed briefly -> restaurant-response Rejected -> cancel + refund."
  Write-Host "Automated (no Kafka):"
  Write-Host "  dotnet test tests/Tadka.Api.Tests --filter FullyQualifiedName~ServiceMode"
  Write-Host "Live script:"
  Write-Host "  powershell -File scripts/demo-service-mode-reject.ps1"
}

Write-Host "Tadka instructor break kit" -ForegroundColor Cyan
Write-Host "Branch tip: main / day-12+ for full demos. Day map: docs/runbooks/DAY-EVOLUTION.md"
Write-Host "Scenario: $Scenario"

switch ($Scenario) {
  "smoke" { Run-Smoke }
  "reject-inline" { Run-RejectInline }
  "poison" { Run-Poison }
  "kafka-down" { Run-KafkaDown }
  "evolution" { Run-Evolution }
  "service-mode-hint" { Run-ServiceModeHint }
  "all" {
    Run-Smoke
    Run-RejectInline
    Run-Poison
    Run-KafkaDown
    Run-Evolution
    Run-ServiceModeHint
  }
}

Write-Host "`nBreak kit complete." -ForegroundColor Green
