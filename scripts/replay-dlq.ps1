# Tadka - DLQ replay for the Day 9 poison-message demo (ADR-051).
# Reads every message currently on order-placed.dlq, extracts the ORIGINAL raw payload each one
# quarantined, and republishes it onto order-placed - the manual recovery step an operator runs
# once the root cause (a bad deploy, a schema break) has actually been fixed. Replaying a message
# that is STILL broken just sends it back to the DLQ after 3 more attempts - fix first, replay after.
#   pwsh scripts/replay-dlq.ps1
#
# Verify recovery with:
#   docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --describe --group tadka-payment

Write-Output "Reading order-placed.dlq (from-beginning, 5s window)..."

$raw = docker exec tadka-kafka /opt/kafka/bin/kafka-console-consumer.sh `
    --bootstrap-server localhost:9092 --topic order-placed.dlq `
    --from-beginning --timeout-ms 5000 2>$null

$lines = $raw -split "`n" | Where-Object { $_.Trim().Length -gt 0 }

if ($lines.Count -eq 0) {
    Write-Output "order-placed.dlq is empty - nothing to replay."
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
        --bootstrap-server localhost:9092 --topic $dlq.originalTopic 2>$null
}

Write-Output ""
Write-Output "Replay complete. If the root cause is fixed, these orders should now settle normally."
Write-Output "If it is NOT fixed, they will fail 3 more times and land back on order-placed.dlq - check the payload before replaying."
