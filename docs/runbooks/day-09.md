# Day 9 — Runbook: Kafka + Outbox/Inbox + Saga (heal the Day-8 wound)

**Branch:** `day-09`  ·  **What's new:** the Day-8 synchronous HTTP bridge is replaced by an **async Kafka backbone** (ADR-027). Order creation writes an `order-placed` row to a **transactional Outbox** (committed with the order, ADR-028); an **OutboxRelay** publishes it to Kafka; the Payment service **consumes** it, charges, and publishes `payment-results`; the monolith consumes that and converges the order (**Saga / choreography**, ADR-029). A down Payment service now means messages **wait**, not lost charges. Consumers are **idempotent** (Inbox + one-charge unique index). New infra: **Kafka** (9092) + **Kafka UI** (8090).

> New here? Read [`README.md`](README.md). Windows PowerShell → `curl.exe`. Two apps + Kafka. Kafka is **off** when `Kafka:BootstrapServers` is unset (so `dotnet test` needs no broker).

## 1. Run it (infra + BOTH apps)

```bash
git checkout day-09
docker compose up -d          # postgres 5432 + replica 5433 + redis 6379 + payment-db 5434 + KAFKA 9092 + kafka-ui 8090
docker compose ps             # wait for tadka-kafka = healthy
```
Two terminals:
```bash
dotnet run --project src/Tadka.Payment.Api    # :5240 — Kafka consumer of order-placed
dotnet run --project src/Tadka.Api            # :5224 — Outbox relay + payment-results consumer
```
Kafka UI: <http://localhost:8090> (watch topics `order-placed` / `payment-results` and consumer-group **lag**).
```bash
RID=a1b2c3d4-0001-4000-8000-000000000001; ITEM=b1b2c3d4-0001-4000-8000-000000000001; CID=c1b2c3d4-0001-4000-8000-000000000001
BODY='{"customerId":"'$CID'","restaurantId":"'$RID'","items":[{"menuItemId":"'$ITEM'","quantity":1}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.9,"longitude":77.7}}'
```

## 2. Happy path — the order settles across Kafka (ADR-027/029)

```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY" | sed -E 's/.*"id":"([^"]+)".*/\1/')
sleep 2
curl -s http://localhost:5224/api/v1/orders/$ORDER | sed -E 's/.*"status":"([^"]+)".*/order: \1/'   # Confirmed
curl -s http://localhost:5240/payments/$ORDER                                                        # {"status":"Completed",...}
```
Flow: `POST /orders` (ms) → outbox row (same txn as the order) → OutboxRelay → Kafka `order-placed` → Payment charges → Kafka `payment-results` → monolith confirms. See it in the order DB:
```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT topic, processed_at IS NOT NULL AS sent FROM ordering.outbox_messages ORDER BY created_at DESC LIMIT 3;"
```

## 3. THE HEADLINE — consumer-down catch-up: messages WAIT, not lost (ADR-027) — heals Day 8

Stop the Payment service (Ctrl+C in its terminal, or kill `:5240`). Then place an order:
```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY" | sed -E 's/.*"id":"([^"]+)".*/\1/')
sleep 3
curl -s http://localhost:5224/api/v1/orders/$ORDER | sed -E 's/.*"status":"([^"]+)".*/order: \1/'   # Created (pending) — NOT lost
# The message is WAITING in Kafka — consumer-group lag > 0:
docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --describe --group tadka-payment
# → order-placed  LAG 1   (CURRENT-OFFSET < LOG-END-OFFSET)
```
Now **restart the Payment service** (`dotnet run --project src/Tadka.Payment.Api`). It resumes from its committed offset, consumes the waiting message, charges, and the order converges:
```bash
sleep 4
curl -s http://localhost:5224/api/v1/orders/$ORDER | sed -E 's/.*"status":"([^"]+)".*/order: \1/'   # Confirmed — caught up
docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --describe --group tadka-payment   # LAG 0
```
> Captured: pending + **LAG 1** while down → restart → **Confirmed**, **LAG 0**. On **Day 8** the same outage **lost** the charge and stranded the order forever. That's the whole point of Kafka + Outbox.

## 4. Idempotent consumer — at-least-once is safe (ADR-028)

A redelivered `order-placed` does not double-charge: the **Inbox** (`payment.inbox_messages`) skips a seen message-id, and the **one-charge unique index** on `payment.payments(order_id)` is the hard guard. Replay the topic from the start with a throwaway group and watch only one payment per order:
```bash
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT order_id, count(*) FROM payment.payments GROUP BY order_id HAVING count(*) > 1;"   # 0 rows — never a double charge
```
(Deterministic proof is in `Tadka.Payment.Api.Tests` — charge twice → one payment, same reference.)

## 5. Saga compensation — a decline cancels the order (ADR-029)

Restart the Payment service with a declining gateway, then place an order:
```powershell
$env:Payment__Gateway__Behavior="Failing"; dotnet run --project src/Tadka.Payment.Api
```
```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY" | sed -E 's/.*"id":"([^"]+)".*/\1/')
sleep 3
curl -s http://localhost:5224/api/v1/orders/$ORDER | sed -E 's/.*"status":"([^"]+)".*/order: \1/'   # Cancelled (compensating action via payment-results=Failed)
```

## 6. The boundary still holds (ADR-024/027) + tests

```bash
grep -rn "FakePaymentGateway\|PaymentDbContext\|Domain.Payments\|IPaymentClient" src/Tadka.Api    # nothing — Ordering talks to Payment ONLY via Kafka events
dotnet test    # 28/28 — monolith 24 (incl. 3 architecture/boundary) + Payment service 4. (Kafka off in tests.)
```

## ✅ Done when
- [ ] `docker compose ps` → `tadka-kafka` healthy; Kafka UI at :8090 shows `order-placed` + `payment-results`.
- [ ] Happy path: `POST /orders` → `Created` (ms) → `Confirmed`; payment `Completed`; the outbox row shows `sent=true`.
- [ ] **Catch-up:** Payment down → order pending + **lag > 0**; restart → order `Confirmed` + **lag 0** (nothing lost).
- [ ] No `order_id` has more than one payment (idempotent); a `Failing` gateway → order `Cancelled` (saga compensation).
- [ ] `grep` over the monolith finds no payment internals / no `IPaymentClient` (Kafka-only).
- [ ] `dotnet test` → **28/28**.

## Troubleshooting
- **`tadka-kafka` name conflict / stuck "starting":** `docker rm -f tadka-kafka tadka-kafka-ui` then `docker compose up -d kafka kafka-ui`.
- **Order never Confirmed:** is the Payment service up and is `tadka-kafka` healthy? Check the monolith log for `OutboxRelay published` and the Payment log for `OrderPlacedConsumer subscribed`. Confirm `Kafka:BootstrapServers=localhost:9092` in both apps' `appsettings.Development.json`.
- **Reset everything:** `docker compose down -v && docker compose up -d` (Kafka data + DBs wiped), then run both apps.

➡️ Next (Day 10): authentication + RBAC across services (JWT validated per-service), the **data-privacy / PII** thread, and extracting the **Delivery** service.
