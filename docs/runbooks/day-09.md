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
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
sleep 2
curl -s http://localhost:5224/api/v1/orders/$ORDER | sed -E 's/.*"status":"([^"]+)".*/order: \1/'   # Confirmed
curl -s http://localhost:5240/payments/$ORDER                                                        # {"status":"Completed",...}
```
Flow: `POST /orders` (ms) → outbox row (same txn as the order) → OutboxRelay → Kafka `order-placed` → Payment charges → Kafka `payment-results` → monolith confirms. See it in the order DB:
```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \"Topic\", \"ProcessedAt\" IS NOT NULL AS sent FROM ordering.outbox_messages ORDER BY \"CreatedAt\" DESC LIMIT 3;"
```

## 3. THE HEADLINE — consumer-down catch-up: messages WAIT, not lost (ADR-027) — heals Day 8

Stop the Payment service (Ctrl+C in its terminal, or kill `:5240`). Then place an order:
```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
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

**Inbox discipline (invariant from Day 9 forward — never unlearn this later):** consumers **check inbox → do the side effect → stamp inbox → commit Kafka offset**. Never stamp inbox *before* the work — a crash would mark the message “done” with no charge/confirm. Payment’s charge path and Ordering’s `payment-results` path both follow this. Handlers stay **idempotent** so redelivery after a mid-handler crash is safe.

> **Day evolution:** Day 9 introduces Kafka/Outbox/Inbox. Day 12 will extract Restaurant and add more topics — the **inbox order does not change**. Full branch map: [`DAY-EVOLUTION.md`](DAY-EVOLUTION.md) (on `main` / day-12+; same invariant applies here).

A redelivered `order-placed` does not double-charge: the **Inbox** (`payment.inbox_messages`) skips a seen message-id, and the **one-charge unique index** on `payment.payments(order_id)` is the hard guard. Replay the topic from the start with a throwaway group and watch only one payment per order:
```bash
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \"OrderId\", count(*) FROM payment.payments GROUP BY \"OrderId\" HAVING count(*) > 1;"   # 0 rows — never a double charge
```
(Deterministic proof is in `Tadka.Payment.Api.Tests` — charge twice → one payment, same reference.)

## 5. Saga compensation — a decline cancels the order (ADR-029)

Restart the Payment service with a declining gateway, then place an order:
```powershell
$env:Payment__Gateway__Behavior="Failing"; dotnet run --project src/Tadka.Payment.Api
```
```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
sleep 3
curl -s http://localhost:5224/api/v1/orders/$ORDER | sed -E 's/.*"status":"([^"]+)".*/order: \1/'   # Cancelled (compensating action via payment-results=Failed)
```

## 6. The boundary still holds (ADR-024/027) + tests

```bash
grep -rn "FakePaymentGateway\|PaymentDbContext\|Domain.Payments\|IPaymentClient" src/Tadka.Api    # nothing — Ordering talks to Payment ONLY via Kafka events
dotnet test    # 34/34 — monolith 25 (incl. 3 architecture/boundary) + Payment service 9 (incl. 5 PoisonMessageTracker). (Kafka off in tests.)
```

## 7. Poison messages: silent loss vs quarantine (ADR-050/051) — **Could-tier / weekday**

> Cut this in class if the clock is tight. The catch-up demo (§3) is the never-cut headline. This beat is weekday lab + `break-kit-day-09.md` Beat 6.

A message that fails processing is NOT retried forever by a plain manual-commit loop —
`Consumer.Consume()` advances the fetch position on every call regardless of commit, so a failed
message is silently, permanently skipped the moment a LATER message's offset commits. Verified
live, not assumed. The fix: bounded retry via explicit `Seek()`, then a Dead Letter Queue.

```bash
pwsh scripts/inject-poison.ps1 -Mode Malformed             # syntactically invalid JSON
LAG() { docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --describe --group "$1"; }
LAG tadka-payment | grep order-placed                       # LAG 1 while retrying, then 0 once it's DLQ'd
docker exec tadka-kafka /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 --topic order-placed.dlq --from-beginning --timeout-ms 5000
```
Payment log shows `will retry` x2 (real redelivery via `Seek`), then `failed 3x — routing to DLQ, partition unblocked`. Place a healthy order right after — it settles normally, proving the partition is genuinely unblocked, not just skipped past.

Once the root cause is fixed, replay the DLQ:
```bash
pwsh scripts/replay-dlq.ps1
```

**Additive-only vs a breaking rename (ADR-050) — different failure shapes, both worth seeing:**
```bash
pwsh scripts/inject-poison.ps1 -Mode SafeExtraField          # an EXTRA unknown field — processes fine, no code change needed
pwsh scripts/inject-poison.ps1 -Mode MissingRequiredField    # Currency renamed to CurrencyCode — ALSO processes fine, NO error, NO DLQ entry
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \"OrderId\", currency FROM payment.payments ORDER BY \"CreatedAt\" DESC LIMIT 1;"   # currency = INR (a DB column default silently filled the gap)
```
The rename case is the sharpest lesson of the day: it throws nothing and DLQs nothing. Schema
discipline (never rename/remove a field) is a code-review-time rule — no runtime mechanism here
can catch it for you.

## 8. Full offset reset + replay — the Inbox dedup, the real test (ADR-028)

```bash
# stop the Payment service, wait ~10s for the consumer group to go inactive, then:
docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --group tadka-payment --topic order-placed --reset-offsets --to-earliest --execute
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT count(*) FROM payment.payments;"   # note this BEFORE restarting
dotnet run --project src/Tadka.Payment.Api    # reprocesses every message from offset 0
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \"OrderId\", count(*) FROM payment.payments GROUP BY \"OrderId\" HAVING count(*) > 1;"   # 0 rows
```
The payments count is unchanged after a FULL replay of the entire topic — the Inbox (ADR-028) dedups every already-processed message, no matter how far back the replay goes.

## ✅ Done when
- [ ] `docker compose ps` → `tadka-kafka` healthy; Kafka UI at :8090 shows `order-placed` + `payment-results`.
- [ ] Happy path: `POST /orders` → `Created` (ms) → `Confirmed`; payment `Completed`; the outbox row shows `sent=true`.
- [ ] **Catch-up:** Payment down → order pending + **lag > 0**; restart → order `Confirmed` + **lag 0** (nothing lost).
- [ ] No `order_id` has more than one payment (idempotent); a `Failing` gateway → order `Cancelled` (saga compensation).
- [ ] `grep` over the monolith finds no payment internals / no `IPaymentClient` (Kafka-only).
- [ ] `dotnet test` → **34/34**.
- [ ] `inject-poison.ps1 -Mode Malformed`: 2x retry then routed to `order-placed.dlq`; a healthy order right after settles normally.
- [ ] `inject-poison.ps1 -Mode MissingRequiredField`: processes with NO error and NO DLQ entry — the payment's currency silently defaults to `INR`.
- [ ] Full offset reset + replay: payments count unchanged, zero duplicate `OrderId` rows.

## Troubleshooting
- **`tadka-kafka` name conflict / stuck "starting":** `docker rm -f tadka-kafka tadka-kafka-ui` then `docker compose up -d kafka kafka-ui`.
- **Order never Confirmed:** is the Payment service up and is `tadka-kafka` healthy? Check the monolith log for `OutboxRelay published` and the Payment log for `OrderPlacedConsumer subscribed`. Confirm `Kafka:BootstrapServers=localhost:9092` in both apps' `appsettings.Development.json`.
- **Reset everything:** `docker compose down -v && docker compose up -d` (Kafka data + DBs wiped), then run both apps.

➡️ Next (Day 10): authentication + RBAC across services (JWT validated per-service), the **data-privacy / PII** thread, and extracting the **Delivery** service.
