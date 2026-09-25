# Day 9 — Runbook: Kafka + Outbox/Inbox + Saga (heal the Day-8 wound)

**Branch:** `day-09`  ·  **What changed since Day 8:** [`docs/changelog.md`](../changelog.md). **What's new:** the Day-8 synchronous HTTP bridge is replaced by an **async Kafka backbone** (ADR-027). Order creation writes an `order-placed` row to a **transactional Outbox** (committed with the order, ADR-028); an **OutboxRelay** publishes it to Kafka; the Payment service **consumes** it, charges, and publishes `payment-results`; the monolith consumes that and converges the order (**Saga / choreography**, ADR-029). A down Payment service now means messages **wait**, not lost charges. Consumers are **idempotent** (Inbox + one-charge unique index). New infra: **Kafka** (9092) + **Kafka UI** (8090).

> New here? Read [`README.md`](README.md). Windows PowerShell → `curl.exe`. Two apps + Kafka. Kafka is **off** when `Kafka:BootstrapServers` is unset (so `dotnet test` needs no broker).

## 1. Run it (infra + BOTH apps)

**Fresh start — wipe everything and recreate from scratch.** Run this once at the start of the day, or any time you want a guaranteed-clean slate (old orders, old topics, old consumer-group offsets — all gone, not just stopped):
```bash
git checkout day-09
docker compose down -v        # stops + removes all 6 containers AND their volumes (Postgres data, Kafka log) — a real wipe, not just a stop
docker compose up -d          # postgres 5432 + replica 5433 + redis 6379 + payment-db 5434 + KAFKA 9092 + kafka-ui 8090
```
Wait for Kafka specifically, not just "containers running" — its own startup (broker init) is slower than Postgres or Redis, and a plain `docker compose ps` doesn't block on it:
```bash
until docker inspect tadka-kafka --format "{{.State.Health.Status}}" | grep -q healthy; do sleep 3; done
```
**Pre-create the three Kafka topics before starting either app.** Skip this and you will hit a real, first-run-only trap verified live on this exact branch: a freshly wiped broker has no topics yet, and each app's consumer subscribes to its topic the instant it starts. If it starts before the *other* app has ever published anything, you'll see `Confluent.Kafka.ConsumeException: Subscribed topic not available: <topic>: Broker: Unknown topic or partition` logged roughly once a second. It's harmless — the consumer loop catches it, retries, and picks the topic up the moment it exists — but it looks like a crash on a first run, and it isn't one. Pre-creating the topics avoids the noise entirely instead of just tolerating it:
```bash
for t in order-placed payment-results order-placed.dlq; do
  docker exec tadka-kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --create --if-not-exists --topic $t --partitions 1 --replication-factor 1
done
```
Now start the two apps in two terminals, in either order — each one connects to Kafka independently, there's no startup-ordering dependency between them:
```bash
dotnet run --project src/Tadka.Payment.Api    # :5240 — Kafka consumer of order-placed
dotnet run --project src/Tadka.Api            # :5224 — Outbox relay + payment-results consumer
```
**Give each app 15-20 seconds after its own "Application started" log line before you trust it.** First-run JIT, the EF migration check, and joining its Kafka consumer group all take real wall-clock time — a few seconds, not milliseconds — and this runbook's `sleep` values later on all assume both apps are already fully up and idle, not mid-startup. This matters most every time you **restart** either app later in this runbook (§3, §5, §8) — give the same 15-20 seconds after each restart before checking status, not just at the very start of the day.

Kafka UI is a read-only window into the broker — open it and leave it open in a browser tab for the rest of this runbook, it's the fastest way to see lag and message flow without typing CLI commands each time: <http://localhost:8090> (watch topics `order-placed` / `payment-results` and consumer-group **lag**).

The same order-placement body from Day 7/8, reused everywhere below so every beat is comparing apples to apples:
```bash
RID=a1b2c3d4-0001-4000-8000-000000000001; ITEM=b1b2c3d4-0001-4000-8000-000000000001; CID=c1b2c3d4-0001-4000-8000-000000000001
BODY='{"customerId":"'$CID'","restaurantId":"'$RID'","items":[{"menuItemId":"'$ITEM'","quantity":1}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.9,"longitude":77.7}}'
```

## 2. Happy path — the order settles across Kafka (ADR-027/029)

**The story (say this before any command):** with both services and Kafka up, an order should still feel instant from the customer's side — `POST /orders` returns in milliseconds, exactly like Day 7/8 — but everything after that response is now flowing through a broker instead of an in-process queue or an HTTP call. This beat just proves the happy path still converges; the interesting beats are the failure ones that follow.

Place an order and capture its id, same shape as every prior day's demo:
```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
sleep 2
curl -s http://localhost:5224/api/v1/orders/$ORDER | sed -E 's/.*"status":"([^"]+)".*/order: \1/'   # Confirmed
curl -s http://localhost:5240/payments/$ORDER                                                        # {"status":"Completed",...}
```
The `sleep 2` exists because this is now an **asynchronous, multi-hop** flow — the order row commits, the outbox relay has to notice and publish it, Payment has to consume and charge, and the monolith has to consume the result — each hop adds a small, real delay that a synchronous call (Day 7/8) didn't have. Captured live on this branch, steady-state (not the very first order after a fresh start, which pays extra JIT/first-query warmup): `POST /orders` itself returns in **~150-180 ms**, and the order settles to `Confirmed` roughly **600 ms to 1.1 s after that** — so `sleep 2` is a comfortable margin, not a tight one, and you'll usually see `Confirmed` well before it.

Flow: `POST /orders` (ms) → outbox row (same txn as the order) → OutboxRelay → Kafka `order-placed` → Payment charges → Kafka `payment-results` → monolith confirms. See it in the order DB — the `Topic` column tells you which outbox row this is, and `ProcessedAt IS NOT NULL` (aliased `sent`) tells you whether the relay has actually pushed it to Kafka yet, as opposed to it still sitting unclaimed in the table:
```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \"Topic\", \"ProcessedAt\" IS NOT NULL AS sent FROM ordering.outbox_messages ORDER BY \"CreatedAt\" DESC LIMIT 3;"
```
`sent=true` on your order's row is the proof that the outbox → Kafka handoff actually happened, not just that the order committed. If you see `sent=false` moments after placing the order, don't panic — the relay polls on an interval, it isn't instantaneous; if it's still `false` after several seconds, that's when to suspect the relay isn't running or Kafka isn't reachable.

**A detail worth knowing before it bites you (ADR-028):** the relay claims a batch of unsent rows with `SELECT … FOR UPDATE SKIP LOCKED` and holds that row lock open for as long as the Kafka publish inside the same transaction takes — so a slow or unreachable broker holds the lock, not just delays the publish. The producer's `MessageTimeoutMs`/`RequestTimeoutMs` is set to **10 s** specifically to bound that window, instead of `librdkafka`'s far more generous 300 s default — a genuine broker outage holds the claimed rows' locks for single-digit seconds, not five minutes. This is a configured value from the codebase, not something measured live, but it's the number to know if you ever see the outbox table's rows apparently "stuck" locked during a Kafka blip.

## 3. THE HEADLINE — consumer-down catch-up: messages WAIT, not lost (ADR-027) — heals Day 8

**The story (say this before any command):** this is the beat the whole day is built around. On Day 8, if the Payment service was down when an order was placed, the synchronous HTTP call simply failed and the charge was **gone** — the queued work item was consumed off the in-memory queue and there was no way to get it back. Today we're going to do the exact same thing — kill Payment, place an order — and show that instead of losing the charge, it just **waits**. That's the entire value of putting a durable broker between the two services.

BREAK — stop the Payment service (Ctrl+C in its terminal, or kill whatever's listening on `:5240`) so there is nothing consuming `order-placed` right now. Then place an order exactly as you did in the happy path:
```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
sleep 3
curl -s http://localhost:5224/api/v1/orders/$ORDER | sed -E 's/.*"status":"([^"]+)".*/order: \1/'   # Created (pending) — NOT lost
```
The order stays `Created` — not an error, not a 500, not a dropped request. `POST /orders` still returned in milliseconds because the monolith only had to write the order row and the outbox row in one local transaction; it never needed Payment to be reachable at all. The order is *pending* because nobody has consumed its `order-placed` event yet — which is exactly what we check next.

**How we identified it** — don't take "it's fine" on faith; look at the consumer-group lag directly. The message is sitting in Kafka, unconsumed:
```bash
docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --describe --group tadka-payment
# → order-placed  LAG 1   (CURRENT-OFFSET < LOG-END-OFFSET)
```
**Reading the lag output:** `LOG-END-OFFSET` is how many messages have ever been published to that partition; `CURRENT-OFFSET` is how far the `tadka-payment` consumer group has actually committed past. `LAG` is the difference — `LAG 1` here means exactly one message (the order you just placed) is published but not yet consumed. Lag is the single most important number in a Kafka-backed system for exactly this reason: it's a live count of "how much work is queued up and unprocessed," and it's what an on-call engineer would alert on in production, not a raw exception count.

FIX — restart the Payment service. It reconnects to Kafka, rejoins the `tadka-payment` consumer group, and resumes from its **last committed offset** — it does not need to be told what it missed, the broker already knows:
```bash
dotnet run --project src/Tadka.Payment.Api
```
Give it real time to actually come back up before you check anything — a restart isn't instant. Consumer-group **rebalance** alone (the group going from "one member" to "zero" to "one member again") takes several seconds on top of the app's own JIT/EF-migration-check startup, and checking too early just shows you a stale in-progress state, not a wrong one. **Wait ~15-20 seconds after the "Application started" log line**, then check both the order status and the lag:
```bash
curl -s http://localhost:5224/api/v1/orders/$ORDER | sed -E 's/.*"status":"([^"]+)".*/order: \1/'   # Confirmed — caught up
docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --describe --group tadka-payment   # LAG 0
```
If you see `order: Created` still, or the describe output says `Warning: Consumer group 'tadka-payment' is rebalancing`, that's not a failure — wait another 5-10 seconds and check again, this was verified live and settled well within 20 seconds total every time.
> Captured: pending + **LAG 1** while down → restart → **Confirmed**, **LAG 0**. On **Day 8** the same outage **lost** the charge and stranded the order forever. That's the whole point of Kafka + Outbox.

**Outcome interpretation — why this matters more than it looks:** `LAG 1 → LAG 0` and `Created → Confirmed` are two views of the same fact, but the lag number is the one that scales — with one order it's a curiosity, with a thousand orders queued during an outage it's the metric that tells you whether the system is recovering or falling further behind. Compare this directly to Day 8 §4 (`docs/runbooks/day-08.md`): there, a Payment outage left the order **permanently** `Created` with no mechanism to ever recover it short of a manual fix. Here, the exact same outage self-heals the moment the consumer comes back — nothing about the order-placement code changed, only the transport between the two services.

## 4. Idempotent consumer — at-least-once is safe (ADR-028)

**The story (say this before any command):** durability alone isn't enough — Kafka's at-least-once delivery guarantee means a message can be delivered **more than once** (a consumer crash between processing and committing its offset is the classic case), and for a payment, processing the same charge twice is a real-money bug, not a cosmetic one. This beat proves the Inbox pattern makes redelivery safe.

**Inbox discipline (invariant from Day 9 forward — never unlearn this later):** consumers **check inbox → do the side effect → stamp inbox → commit Kafka offset**. Never stamp inbox *before* the work — a crash would mark the message "done" with no charge/confirm. Payment's charge path and Ordering's `payment-results` path both follow this. Handlers stay **idempotent** so redelivery after a mid-handler crash is safe.

> **Day evolution:** Day 9 introduces Kafka/Outbox/Inbox. Day 12 will extract Restaurant and add more topics — the **inbox order does not change**. Full branch map: [`DAY-EVOLUTION.md`](DAY-EVOLUTION.md) (on `main` / day-12+; same invariant applies here).

A redelivered `order-placed` does not double-charge: the **Inbox** (`payment.inbox_messages`) skips a seen message-id, and the **one-charge unique index** on `payment.payments(order_id)` is the hard guard underneath it — even if the Inbox check somehow got bypassed, the database itself would reject a second row for the same order. Query for any order with more than one payment row, across every order placed today:
```bash
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \"OrderId\", count(*) FROM payment.payments GROUP BY \"OrderId\" HAVING count(*) > 1;"   # 0 rows — never a double charge
```
Zero rows is the proof — it means across every order this session, `GROUP BY OrderId` never found a duplicate. That query is deliberately the same shape you'd run in production against real traffic; it doesn't require you to have staged the redelivery yourself, it just proves the invariant holds for whatever has actually happened.
(Deterministic proof is in `Tadka.Payment.Api.Tests` — charge twice → one payment, same reference.)

## 5. Saga compensation — a decline cancels the order (ADR-029)

**The story (say this before any command):** the happy path (§2) and the catch-up path (§3) both end in `Confirmed`. But payments fail for real reasons — a declined card, an expired one — and there's no shared database transaction spanning Order and Payment any more to roll back automatically. The Saga pattern (choreography, ADR-029) is what stands in for that rollback: Payment publishes `Failed`, and the monolith reacts to that event with its own **compensating action** — cancelling the order.

Restart the Payment service with the fake gateway set to decline every charge. Wait the same ~15-20 seconds as always after a restart before doing anything else — the consumer group has to rejoin, same as §3:
```powershell
$env:Payment__Gateway__Behavior="Failing"; dotnet run --project src/Tadka.Payment.Api
```
Place an order against the now-declining gateway the same way as every other beat:
```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
sleep 10
curl -s http://localhost:5224/api/v1/orders/$ORDER | sed -E 's/.*"status":"([^"]+)".*/order: \1/'   # Cancelled (compensating action via payment-results=Failed)
```
If it still shows `Created`, wait a few more seconds and check again — same settling behaviour as every other beat that reacts to Payment's output, not a different failure.

**Outcome interpretation:** `Cancelled` here is not an error state, it's a **correct, expected outcome** of the saga — the same `PaymentFailed → order.Cancel()` reaction that existed since Day 7/8, just driven by a Kafka event instead of an in-process call or a synchronous HTTP response. Compare the three days: Day 7 cancelled the order in-process (§6 of that runbook); Day 8 cancelled it via a synchronous HTTP response; Day 9 cancels it via an asynchronous event the monolith consumes independently — the *business rule* ("a decline cancels the order") hasn't moved once across three architectural rewrites, only the mechanism carrying the news of the decline has.

**Reset the gateway back to normal before moving on.** Every beat from here on (§6-§8) assumes a healthy gateway that confirms orders — skip this and later orders keep coming back `Cancelled`, which looks like a new bug when it's really just this switch left on. Stop Payment (Ctrl+C) and restart it plain, no `Behavior` env var, same ~15-20 second wait as always:
```bash
dotnet run --project src/Tadka.Payment.Api
```

## 6. The boundary still holds (ADR-024/027) + tests

The Payment extraction from Day 8 didn't just survive the Kafka rewrite, it's what made the rewrite a drop-in replacement instead of a redesign — Ordering never had a compile-time reference to Payment to begin with, so swapping the transport underneath didn't touch Ordering's code shape at all. Confirm the grep is still clean — **exclude `Migrations/` and comment lines**, or you'll see false-hit noise from frozen pre-extraction EF migration snapshots and an explanatory code comment, neither of which is a real compile-time dependency:
```bash
grep -rn "FakePaymentGateway\|PaymentDbContext\|Domain.Payments\|IPaymentClient" src/Tadka.Api --exclude-dir=Migrations | grep -v '^\S*:\s*//'    # nothing — Ordering talks to Payment ONLY via Kafka events
```
No hits means Ordering has zero compile-time knowledge of Payment's gateway, database, or even the `IPaymentClient` interface Day 8 introduced for the HTTP bridge — that interface is gone entirely, replaced by "publish an event and react to another one." Run the full suite to confirm none of this broke anything the earlier days already covered:
```bash
dotnet test    # 34/34 — monolith 25 (incl. 3 architecture/boundary) + Payment service 9 (incl. 5 PoisonMessageTracker). (Kafka off in tests.)
```
The count grew from Day 8's 25/25 to 34/34: the architecture/boundary tests now assert against Kafka-based messaging instead of `IPaymentClient`, and the new `PoisonMessageTracker` tests (§7 below) are unit tests with no live Kafka dependency — the project's standing rule is that `dotnet test` must stay green with **no broker running**, which is why Kafka is configured off by default in the test environment.

## 7. Poison messages: silent loss vs quarantine (ADR-050/051) — **Could-tier / weekday**

> Cut this in class if the clock is tight. The catch-up demo (§3) is the never-cut headline. This beat is weekday lab + `break-kit-day-09.md` Beat 6.

**The story (say this before any command):** at-least-once delivery and idempotency (§3, §4) solve "the consumer was down" and "the same message arrived twice." Neither solves a third failure mode: a message that the consumer picks up but **cannot process at all** — malformed JSON, a business exception on every attempt. Naively, `Consumer.Consume()` advances the fetch position on every call **regardless of whether you committed** — so a plain manual-commit loop that logs-and-continues on failure doesn't retry that message, it silently, permanently skips it the instant a *later* message's offset commits. This was verified live against this exact codebase, not assumed: a poison message failed once, a healthy order placed right after committed an offset past both messages, and the group's lag went straight back to `0` — with the poisoned order sitting at `Created` forever and nothing anywhere logging that it had been abandoned.

BREAK — inject a malformed message directly onto the `order-placed` topic. Run this in **PowerShell**, not the bash shell you've been using for the `curl`/`docker exec` commands above — it's a `.ps1` script, and it runs fine on plain Windows PowerShell 5.1 (`pwsh`/PowerShell 7 is not required, and isn't installed on every machine):
```powershell
.\scripts\inject-poison.ps1 -Mode Malformed             # syntactically invalid JSON
```
Watch the lag while the consumer is retrying it — a small helper function to save retyping the describe command:
```bash
LAG() { docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --describe --group "$1"; }
LAG tadka-payment | grep order-placed                       # LAG 1 while retrying, then 0 once it's DLQ'd
```
**Outcome interpretation:** `LAG 1` here doesn't mean the same thing it meant in §3 — there it meant "nobody is consuming yet," here it means "the consumer is actively stuck retrying the same offset." The fix, per `PoisonMessageTracker` (ADR-051): on a handler exception the consumer explicitly `Seek()`s back to the failed offset (forcing real redelivery, not the silent skip described above) up to `MaxAttempts` (3) times, with a fixed 300 ms gap between attempts — so three attempts land in roughly 900 ms total, a number derived directly from that configured constant, not a live measurement. On the third failure it publishes the original, unmodified payload plus the error text to `order-placed.dlq`, then commits the original offset — which is what finally drops the lag back to `0`, because "0" now means "quarantined," not "processed."

Read the DLQ to confirm the poisoned payload landed there intact:
```bash
docker exec tadka-kafka /opt/kafka/bin/kafka-console-consumer.sh --bootstrap-server localhost:9092 --topic order-placed.dlq --from-beginning --timeout-ms 5000
```
Payment's own log is the other half of the proof — it should show `will retry` twice (real redelivery via `Seek`, not a silent skip), then `failed 3x — routing to DLQ, partition unblocked`. Place a healthy order right after the poison message and confirm it settles normally — this is the part that actually proves the *partition* is unblocked, not merely that the poison message stopped erroring:
```bash
# place a normal order here using the same $BODY as every other beat, then confirm it converges to Confirmed as usual
```

**Additive-only vs a breaking rename (ADR-050) — different failure shapes, both worth seeing:** a DLQ only catches messages that **throw**. A message that deserializes fine but silently drops a renamed field is a different, sharper failure — it throws nothing and DLQs nothing. (PowerShell again.)
```powershell
.\scripts\inject-poison.ps1 -Mode SafeExtraField          # an EXTRA unknown field — processes fine, no code change needed
.\scripts\inject-poison.ps1 -Mode MissingRequiredField    # Currency renamed to CurrencyCode — ALSO processes fine, NO error, NO DLQ entry
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \"OrderId\", currency FROM payment.payments ORDER BY \"CreatedAt\" DESC LIMIT 1;"   # currency = INR (a DB column default silently filled the gap)
```
**Outcome interpretation:** `SafeExtraField` processing cleanly is expected and fine — `System.Text.Json`'s default behaviour ignores unmapped properties, and that's the correct, desired forward-compatibility behaviour for an additive change. `MissingRequiredField` processing *just as cleanly* is the actual lesson: the missing constructor parameter bound to `null`, and a Postgres column default (`HasDefaultValue("INR")`) silently filled it in on `INSERT` — the payment "succeeded" with a currency nobody actually specified, and nothing (not the consumer, not the database, not a log line) ever surfaced that anything was wrong. Schema discipline — never rename or remove a field on a shared event, only ever add — is a **code-review-time rule**; no runtime mechanism shown in this runbook, DLQ included, can catch a rename for you.

Once the root cause is fixed, replay the DLQ — every quarantined message gets republished to `order-placed` and reprocessed from scratch (PowerShell):
```powershell
.\scripts\replay-dlq.ps1
```
A message that's still genuinely broken simply fails its 3 attempts again and lands back on the DLQ — which is the correct, safe outcome, not a bug in the replay script.

## 8. Full offset reset + replay — the Inbox dedup, the real test (ADR-028)

**The story (say this before any command):** §4 proved the Inbox stops a *single* redelivered message from double-charging. This beat is the stress-test version of that same claim — replay the **entire topic from offset 0**, every message ever published, and confirm the payments table doesn't grow by a single row.

Stop the Payment service, then reset the group's offset back to the very beginning of the topic — but a still-active consumer-group registration refuses an offset reset, and it takes **longer than you'd guess** to go inactive. This is the client library's session timeout, not the broker being slow: `Confluent.Kafka`'s default `session.timeout.ms` is 45 seconds, and verified live on this exact branch, the reset command failed on retries at 10s and at 22s elapsed before finally succeeding around a 60-70 second total wait. Don't fight it — poll instead of guessing a fixed sleep:
```bash
# stop the Payment service first, then:
until docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --group tadka-payment --topic order-placed --reset-offsets --to-earliest --execute; do
  echo "group still active, retrying in 5s..."; sleep 5
done
```
Note the payment count **before** restarting, so you have something to compare against once the full replay finishes:
```bash
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT count(*) FROM payment.payments;"   # note this BEFORE restarting
```
Restart the Payment service — with its offset reset to earliest, it will re-consume **every** `order-placed` message that has ever been published to the topic, from the very first order of the day. Give it the usual ~15-20 seconds to fully come up and finish reprocessing before checking anything:
```bash
dotnet run --project src/Tadka.Payment.Api    # reprocesses every message from offset 0
```
Check for duplicates the same way as §4, across the entire replayed history this time, not just one message:
```bash
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \"OrderId\", count(*) FROM payment.payments GROUP BY \"OrderId\" HAVING count(*) > 1;"   # 0 rows
```
**Outcome interpretation:** the payments count is unchanged after a full replay of the entire topic, and zero orders show more than one payment row — the Inbox (ADR-028) dedups every already-processed message, no matter how far back the replay goes. This is the strongest version of the idempotency claim in the whole runbook: it isn't just "redelivery of the one message we just staged is safe," it's "reprocessing the entire history of the system from scratch produces exactly the same state as processing it once."

## 9. Wiring reference + cross-language comparison

**Where the code lives:** monolith `Infrastructure/Messaging/*` (the `order-placed` producer + the `payment-results` consumer), `Data/Outbox/*` + `OutboxRelay` (the claim-and-publish loop, `FOR UPDATE SKIP LOCKED`); Payment service `Messaging/*` (the `order-placed` consumer + the `payment-results` producer), `PoisonMessageTracker.cs` + `OrderPlacedConsumer.cs` (the retry/DLQ logic from §7). Both services' `inbox_messages` tables and dedup checks are what §4/§8 exercise. Full design reasoning: ADR-027 (Kafka as the backbone), ADR-028 (Outbox/Inbox), ADR-029 (Saga/choreography), ADR-050 (envelope versioning), ADR-051 (DLQ).

**If you'd build this same system in Java or Node instead of .NET**, the pattern is identical (at-least-once delivery, an idempotent consumer, a durable outbox, choreographed compensation) — what changes is the tooling. Full detail in [`docs/learn/cross-stack-async-messaging.md`](../learn/cross-stack-async-messaging.md); the shape of it:

| Concern | .NET (this repo, `Confluent.Kafka`) | Java (Spring Kafka) | Node (`kafkajs`) | Go |
|---|---|---|---|---|
| Manual offset commit | Explicit — commit only after the handler succeeds | Spring defaults to `AckMode.BATCH` + `enable-auto-commit=true` — you must set `AckMode.MANUAL`/`MANUAL_IMMEDIATE` or you inherit the exact commit-before-processing trap this runbook demonstrates | `autoCommit` defaults to `true` on a timer, same trap — set `autoCommit: false`, call `commitOffsets()` after `eachMessage` completes | Hand-rolled via `SetOffset`/manual commit on `segmentio/kafka-go` or `confluent-kafka-go` — no framework default to fight |
| Transactional outbox | Hand-rolled: `outbox_messages` table + `OutboxRelay` `BackgroundService`, `FOR UPDATE SKIP LOCKED` for multi-pod safety | Two real framework options: **Debezium** (reads the Postgres WAL directly, no polling relay at all) or **Spring Modulith's Event Publication Registry** (near-free outbox semantics on top of `ApplicationEventPublisher`) | No framework gives this for free — hand-rolled the same shape as .NET, though `SKIP LOCKED` isn't expressible through most Prisma query builders, so the claim query drops to `$queryRaw` | Hand-rolled; Debezium is stack-agnostic since it reads the WAL, not the producer's code |
| DLQ / poison messages | Hand-rolled `PoisonMessageTracker` — bounded `Seek()` retry then publish-and-commit to a `.dlq` topic | Built in: `DeadLetterPublishingRecoverer` + `SeekToCurrentErrorHandler` (or the newer `DefaultErrorHandler` with backoff) | No built-in helper — this exact retry-then-republish shape is the idiomatic hand-rolled approach | Hand-rolled around `SetOffset`/`Seek`, same shape as .NET |
| Saga / choreography | `PaymentResultsConsumer` reacts to `payment-results`, drives the existing order state machine | `@KafkaListener` method reacting to the same topic — same pattern, different syntax | `eachMessage` callback — same pattern again | A consumer loop reacting to the topic — same pattern |

**What doesn't change across any of these stacks:** the partition key discipline (`orderId`, so one order's events stay ordered) is a Kafka protocol decision, not a library one, and the multi-instance danger for an outbox relay (`WHERE processed_at IS NULL LIMIT 50` letting N pods claim the same rows) is identical in every language — the fix is `SKIP LOCKED`, leader election, or CDC, an architectural choice independent of runtime.

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
- **`ConsumeException: Subscribed topic not available` spamming the log on first startup:** expected and harmless on a fresh broker if you skipped the topic pre-create step in §1 — it self-heals the moment the topic is created by its first producer. Not a crash; the consumer loop keeps polling. Pre-create the topics next time to avoid the noise.
- **Order never Confirmed:** is the Payment service up and is `tadka-kafka` healthy? Check the monolith log for `OutboxRelay published` and the Payment log for `OrderPlacedConsumer subscribed`. Confirm `Kafka:BootstrapServers=localhost:9092` in both apps' `appsettings.Development.json`. If you *just* started or restarted either app, also just wait — see the next item.
- **Order/lag looks stuck right after starting or restarting an app:** this is very often just impatience, not a bug — verified live, a restart needs a genuine 15-20 seconds (JIT + EF migration check + Kafka consumer-group rejoin) before its output means anything. `kafka-consumer-groups.sh --describe` printing `Warning: Consumer group '...' is rebalancing` is your confirmation it's still settling, not broken.
- **`inject-poison.ps1` (or any `.ps1` script here) says "command not found" or "pwsh not recognized":** run it as `.\scripts\inject-poison.ps1 ...` in plain Windows PowerShell — none of this repo's scripts need PowerShell 7/`pwsh`, which may not be installed on your machine at all.
- **`inject-poison.ps1` seems to do nothing:** it publishes directly to the raw topic bypassing the API, so nothing shows up via `POST /orders` — watch the Payment service log and the DLQ topic directly, not the orders endpoint.
- **The §6 boundary `grep` shows hits and you were told to expect none:** make sure your command excludes `Migrations/` and comment-only lines (see §6) — old EF migration snapshots from before Payment was extracted still mention `Domain.Payments.Payment`, and one source comment mentions `PaymentDbContext` by name; neither is a real compile-time dependency, and the plain grep without those exclusions will always show a false positive.
- **`--reset-offsets` fails with "group is still active":** this is normal, not a sign anything's wrong — the consumer group registration takes a genuinely long time to expire after you stop the process (`Confluent.Kafka`'s default `session.timeout.ms` is 45 seconds; a live run on this branch needed ~60-70s total before the reset succeeded). Use the polling loop in §8 rather than a single fixed `sleep`.
- **Outbox row stuck `sent=false` for longer than a few seconds:** check the monolith log for `OutboxRelay` errors — either the relay loop isn't running, or the row's claim lock is being held open by a slow Kafka publish (see the callout in §2); `docker compose ps` to confirm `tadka-kafka` is actually healthy, not just "running."
- **Reset everything:** the §1 fresh-start block (`docker compose down -v && docker compose up -d`, then re-create the three topics) is exactly this — run it any time you want a guaranteed-clean slate, not just at the start of the day.

➡️ Next (Day 10): authentication + RBAC across services (JWT validated per-service), the **data-privacy / PII** thread, and extracting the **Delivery** service.
