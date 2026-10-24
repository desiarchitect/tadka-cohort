---
paths:
  - "src/Tadka.Payment.Api/**/*.cs"
  - "tests/Tadka.Payment.Api.Tests/**/*.cs"
---

# Payment service rules (loads when you touch `Tadka.Payment.Api` files)

## Isolation (ADR-024, ADR-026)

- Payment owns its own Postgres on port 5434 and its own migration history. It never references the Orders project or database.
- It talks to Orders only through Kafka: it consumes `order-placed` and publishes `payment-results`.
- No card number or CVV is ever stored or logged. Keep tokens and provider references only.
- Endpoints are minimal endpoints in `Program.cs`. They require a valid JWT, and the service checks the token itself even though a gateway sits in front.

## The pipeline around the gateway (ADR-021, ADR-043)

Every call to the payment gateway goes through `PaymentResiliencePipeline` (`src/Tadka.Payment.Api/Resilience/`). The defaults live in `PaymentOptions`:

| Layer | Default |
|---|---|
| Bulkhead | 10 concurrent charges |
| Retry | 2 retries, exponential with jitter |
| Circuit breaker | opens at ratio 0.5, minimum 5 calls, 30 s window, stays open 60 s |
| Timeout | 2 s per attempt |

## The rule that matters most

- `PaymentDeclinedException` (a normal "no" from the bank) is **final**. It is never retried and never counted by the breaker.
- `PaymentGatewayUnavailableException` and `TimeoutRejectedException` (the gateway is sick) are retried and counted.

Do not widen the predicate, and do not lower the breaker's minimum of 5 calls to 1. A breaker that opens on one blip makes every small hiccup an outage.

## When the gateway is unavailable

`Payment:OnGatewayUnavailable` decides what happens:

- `Compensate` (default): save the payment as Failed and publish `payment-results`. The order is cancelled.
- `Buffer`: save nothing, keep the Kafka message, pause the consumer and charge after the gateway recovers.

The fake gateway has four levers for demos: `Fast`, `Slow`, `Failing` (a decline) and `Outage` (a transport failure), set with `Payment:Gateway:Behavior`.

## Throughput to remember

One partition and one consumer charge about 4 orders per second. More partitions and more Payment instances are the way to scale this, not a bigger timeout.
