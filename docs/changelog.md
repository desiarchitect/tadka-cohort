# Day 14 changelog (since Day 13)

`git checkout day-14`. Previous branch: `day-13`.

## We learned

- Ordering→Payment is **Kafka** (Day 9). The circuit breaker goes on the real **sync** dep: **Payment→gateway**.
- Retry is **transport-only**. Declines (`PaymentDeclinedException`) are never retried. `Outage` ≠ `Failing`.
- **Buffer mode vs Compensate mode (Fix 2 / ADR-043):** on gateway outage, Compensate cancels permanently; Buffer mode deletes the pending payment row and seeks back the Kafka offset so the order retries once the gateway recovers.
- **Graceful degradation:** Redis/replica = performance (fall through). Postgres/money = correctness (fail honest). Never stale money.

## Architecture

- Polly complete: bulkhead → retry (jittered, transport-only) → circuit breaker → 2s timeout.
- `Payment:OnGatewayUnavailable` lever (`Compensate` | `Buffer`).
- Backpressure / load-shed levers exist (`Backpressure:MaxConcurrent`, `LoadShed:Enabled`).

## Code vs Day 13

| Area | What changed |
|---|---|
| `PaymentResiliencePipeline` | Retry + breaker (ADR-043) |
| `PaymentGatewayUnavailableException` vs `PaymentDeclinedException` | Transport vs business |
| `FakePaymentGateway` | `Outage` behavior |
| `OnGatewayUnavailable` | `Compensate` vs `Buffer` mode + consumer seek-back |
| `tadka.payment.circuit_transitions` | Metric |
| `PaymentServiceGatewayUnavailableTests` | 4 unit tests for Buffer/Compensate/ghost-row protection |

ADRs **043, 044**.
