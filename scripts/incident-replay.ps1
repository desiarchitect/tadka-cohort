# Timed multi-fault cascade for the capstone Game Day (Phase 10).
# Runs against a live compose stack. Does not change app code mid-run except docker stop/start.
#
#   pwsh scripts/incident-replay.ps1
#   pwsh scripts/incident-replay.ps1 -SkipWaits   # for dry script check

param([switch]$SkipWaits)

function Wait-Sec([int]$s) {
    if ($SkipWaits) { Write-Host "(skip wait ${s}s)"; return }
    Write-Host "Waiting ${s}s..."
    Start-Sleep -Seconds $s
}

Write-Host "=== Tadka Incident Replay ==="
Write-Host "T+0  Payment should be Slow (set Payment__Gateway__Behavior=Slow before starting this script if not already)."
Write-Host "     Start k6 average-load in another terminal: k6 run k6/average-load.js"
Wait-Sec 30

Write-Host "T+30s  Stopping Redis (cache + SSE + flags/rate-limit if Redis-backed)..."
docker compose stop redis
Wait-Sec 30

Write-Host "T+60s  Stopping Kafka (outbox backlog, payment lag)..."
docker compose stop kafka
Wait-Sec 30

Write-Host "T+90s  Recovery: start Kafka then Redis..."
docker compose start kafka
Wait-Sec 15
docker compose start redis
Wait-Sec 15

Write-Host "=== Replay complete. Students should have used metrics/logs/traces to recover. ==="
Write-Host "Verify: outbox drained, payments settle, menu cache warms."
