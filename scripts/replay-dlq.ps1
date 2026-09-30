# Tadka - DLQ replay (ADR-051).
# Reads every message currently on a dead-letter topic, extracts the ORIGINAL raw payload each one
# quarantined, and republishes it onto the topic it came from - the manual recovery step an operator runs
# once the root cause (a bad deploy, a schema break, a database outage) has actually been fixed.
# Replaying a message that is STILL broken just sends it back to the DLQ after 3 more attempts: fix first,
# replay after. The handlers are idempotent (Inbox + unique indexes), so replaying twice is harmless.
#
#   pwsh scripts/replay-dlq.ps1                              # order-placed.dlq (default)
#   pwsh scripts/replay-dlq.ps1 -DlqTopic order-confirmed.dlq
#   Dead-letter topics: order-placed.dlq, refund-requested.dlq (Payment) · payment-results.dlq,
#   payment-refunded.dlq (monolith) · order-confirmed.dlq (Delivery)
param([string]$DlqTopic = "order-placed.dlq")

Write-Output "Reading $DlqTopic (from-beginning, 5s window)..."

$raw = docker exec tadka-kafka /opt/kafka/bin/kafka-console-consumer.sh `
    --bootstrap-server localhost:9092 --consumer.config /etc/kafka/docker/client.properties --topic $DlqTopic `
    --from-beginning --timeout-ms 5000 2>$null

$lines = $raw -split "`n" | Where-Object { $_.Trim().Length -gt 0 }

if ($lines.Count -eq 0) {
    Write-Output "$DlqTopic is empty - nothing to replay."
    exit 0
}

Write-Output "Found $($lines.Count) quarantined message(s)."

foreach ($line in $lines) {
    try {
        $dlq = $line | ConvertFrom-Json
    } catch {
        Write-Output "Skipping a line that isn't valid DlqMessage JSON: $line"
        continue
    }

    Write-Output ""
    Write-Output "Replaying to $($dlq.originalTopic): $($dlq.originalPayload)"
    Write-Output "  (originally failed $($dlq.attempts)x: $($dlq.error))"

    $dlq.originalPayload | docker exec -i tadka-kafka /opt/kafka/bin/kafka-console-producer.sh `
        --bootstrap-server localhost:9092 --producer.config /etc/kafka/docker/client.properties --topic $dlq.originalTopic 2>$null
}

Write-Output ""
Write-Output "Replay complete. If the root cause is fixed, these messages should now be processed normally."
Write-Output "If it is NOT fixed, they will fail 3 more times and land back on $DlqTopic - check the payload before replaying."
