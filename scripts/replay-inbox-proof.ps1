# Tadka - full-topic-replay Inbox proof (Day 9, ADR-028).
# Resets the tadka-payment consumer group's offset for order-placed back to the very beginning,
# so EVERY order the Payment service has ever charged gets redelivered from scratch. If Inbox dedup
# (and the one-payment-per-order unique index) is working, the payment count in Postgres is UNCHANGED
# after the replay - at-least-once delivery, exactly-once EFFECT.
#
#   pwsh scripts/replay-inbox-proof.ps1
#
# Prerequisite: at least a few orders already placed and settled (so there's something to replay).
# Payment.Api runs as a local `dotnet run` process (only its DATABASE is a container) - Kafka refuses
# to reset offsets on an ACTIVE consumer group, so this script pauses here and asks you to stop it.

$ErrorActionPreference = "Stop"

function Get-PaymentCount {
    $out = docker exec tadka-payment-db psql -U tadka -d tadka_payment -t -A -c "SELECT COUNT(*) FROM payment.payments;" 2>$null
    return [int]($out.Trim())
}

Write-Output "=== Full-topic-replay Inbox proof ==="

$before = Get-PaymentCount
Write-Output "Payments in Postgres before replay: $before"
if ($before -eq 0) {
    Write-Output "No payments yet - place a few orders first (POST /api/v1/orders via :8080), then re-run this script."
    exit 0
}

Write-Output ""
Write-Output "Stop Tadka.Payment.Api now (Ctrl+C in its terminal) - Kafka refuses to reset offsets on an active"
Write-Output "consumer group. Press Enter here once it's stopped."
Read-Host | Out-Null

Write-Output "Resetting tadka-payment consumer group to earliest on order-placed..."
docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh `
    --bootstrap-server localhost:9092 --command-config /etc/kafka/docker/client.properties --group tadka-payment --topic order-placed `
    --reset-offsets --to-earliest --execute | Out-Null

Write-Output ""
Write-Output "Now restart Tadka.Payment.Api (dotnet run --project src/Tadka.Payment.Api) - watch it redeliver"
Write-Output "every order-placed message from offset 0. Press Enter here once it's back up."
Read-Host | Out-Null

Write-Output "Waiting up to 30s for the replay to drain (lag -> 0)..."
$deadline = (Get-Date).AddSeconds(30)
do {
    Start-Sleep -Seconds 2
    $lagOut = docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh `
        --bootstrap-server localhost:9092 --command-config /etc/kafka/docker/client.properties --describe --group tadka-payment 2>$null
    $stillLagging = $lagOut | Select-String "order-placed" | Where-Object { $_ -notmatch '\s0\s*$' }
} while ($stillLagging -and (Get-Date) -lt $deadline)

$after = Get-PaymentCount
Write-Output ""
Write-Output "Payments in Postgres after replay: $after"

if ($after -eq $before) {
    Write-Output "PASS: replay redelivered every order-placed message, payment count unchanged ($before -> $after)."
    Write-Output "At-least-once delivery + Inbox dedup + the one-payment-per-order unique index = exactly-once EFFECT."
} else {
    Write-Output "UNEXPECTED: payment count changed ($before -> $after). Either the replay is still draining (re-run in a few seconds)"
    Write-Output "or Inbox dedup has regressed - check InboxMessages and the OrderPlacedConsumer dedup check before assuming a real bug."
}
