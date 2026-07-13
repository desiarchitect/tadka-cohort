# Tadka - poison-message injector for the Day 9 DLQ + schema-evolution demo (ADR-050/051).
# Publishes a single raw message directly onto the order-placed topic, bypassing the real
# producer, to simulate a bad deploy landing a message the Payment consumer wasn't built for.
#   pwsh scripts/inject-poison.ps1 -Mode Malformed           # syntactically invalid JSON -> throws, retries, then DLQ
#   pwsh scripts/inject-poison.ps1 -Mode MissingRequiredField # Currency renamed to CurrencyCode -> NO throw, NO retry, NO error anywhere
#   pwsh scripts/inject-poison.ps1 -Mode SafeExtraField       # one EXTRA unknown field -> processes fine (proves additive-only is safe)
#
# Watch the effect with:
#   docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --describe --group tadka-payment

param(
    [ValidateSet("Malformed", "MissingRequiredField", "SafeExtraField")]
    [string]$Mode = "Malformed"
)

$orderId = [guid]::NewGuid().ToString()
$messageId = [guid]::NewGuid().ToString()

$payload = switch ($Mode) {
    "Malformed" {
        # truncated / syntactically invalid JSON - JsonSerializer.Deserialize throws immediately
        '{"MessageId":"' + $messageId + '","OrderId":"' + $orderId + '","Amount":299.00,"Cur'
    }
    "MissingRequiredField" {
        # a bad deploy renamed Currency -> CurrencyCode without a compatibility shim (ADR-050 violation).
        # This is intentionally NOT the "throws" case - see the output note below for why that matters.
        '{"MessageId":"' + $messageId + '","OrderId":"' + $orderId + '","Amount":299.00,"CurrencyCode":"INR"}'
    }
    "SafeExtraField" {
        # additive-only evolution: a genuinely new field the consumer doesn't know about yet
        '{"MessageId":"' + $messageId + '","OrderId":"' + $orderId + '","Amount":299.00,"Currency":"INR","Version":1,"PromoCode":"FESTIVE50"}'
    }
}

Write-Output "Injecting [$Mode] onto order-placed (key=$orderId):"
Write-Output $payload

$key = "$orderId="
$line = "$key$payload"
$line | docker exec -i tadka-kafka /opt/kafka/bin/kafka-console-producer.sh `
    --bootstrap-server localhost:9092 --topic order-placed `
    --property "parse.key=true" --property "key.separator==" 2>$null

Write-Output ""
switch ($Mode) {
    "SafeExtraField" {
        Write-Output "Expected: the order still charges normally - System.Text.Json ignores the unknown PromoCode field."
        Write-Output "This is what 'additive-only' schema evolution buys you: zero code changes needed on either side."
    }
    "Malformed" {
        Write-Output "Expected (a plain manual-commit loop, no retry logic): JsonException on first attempt, the offset is"
        Write-Output "never committed, but Consume() still moves on to the NEXT message - the moment that next message"
        Write-Output "commits, this one is silently skipped FOREVER. The order it belongs to is never charged, no error"
        Write-Output "anywhere. (See break-kit-day-09.md Beat 6 for the captured before/after.)"
        Write-Output "Expected (with the fix, as shipped): 2x 'will retry' (explicit Seek), then '...routing to DLQ',"
        Write-Output "offset commits, partition unblocked, the payload preserved on order-placed.dlq for inspection."
    }
    "MissingRequiredField" {
        Write-Output "Expected: NO exception, NO retry, NO log line at all - it just processes. System.Text.Json binds"
        Write-Output "the missing 'Currency' constructor parameter to null (not an error), and the payments table's"
        Write-Output "column default (HasDefaultValue 'INR', PaymentDbContext.cs) silently fills it in on INSERT."
        Write-Output "The payment 'succeeds' with a currency nobody asked for and nothing ever surfaces the mistake -"
        Write-Output "this is the honest case for why schema-evolution discipline (ADR-050) is a REVIEW-TIME rule,"
        Write-Output "not something the runtime or the DLQ can catch for you."
    }
}
