# Day 9 changelog (since Day 8)

`git checkout day-09`. Previous branch: `day-08`.

## We learned

- Kafka as the **async backbone**. Partition by `orderId`. At-least-once + idempotent consumer.
- **Transactional outbox:** `order-placed` is committed in the order txn, then relayed to Kafka (`FOR UPDATE SKIP LOCKED`).
- **Inbox** dedup = exactly-once *effect*.
- **Saga choreography** (no 2PC): `payment-results` confirm or compensating-cancel.
- HTTP remains for **queries**. A down Payment no longer fails the POST — messages wait (catch-up demo).

## Architecture

- Day-8 `IPaymentClient` **removed** from the write path.
- Topics: `order-placed`, `payment-results`.
- Compose: Kafka (KRaft) + Kafka UI :8090. Kafka **off** when `Kafka:BootstrapServers` unset (tests).

## Code vs Day 8

| Area | What changed |
|---|---|
| `Infrastructure/Messaging/*` | OutboxRelay, producers, consumers |
| `Data/Messaging/*` | Outbox / Inbox tables |
| Payment | `OrderPlacedConsumer` |
| `tests/.../Architecture/BoundaryTests.cs` | No cross-schema FK; Ordering ↛ Payment |

ADRs **027, 028, 029**.
