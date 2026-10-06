# Runbook — Day 14: Resilience & Chaos Engineering

**What changed since Day 13:** [`docs/changelog.md`](../changelog.md).

Day 13 made the system observable; Day 14 makes it **survivable** — and measures every failure on the Day-13 dashboards. The circuit breaker + retry complete the Payment→gateway pipeline (ADR-043); dependency classification + graceful degradation are the policy (ADR-044).

> **The reframe:** the Ordering→Payment hop is Kafka (Day 9) — a down Payment just makes messages wait. The breaker lives on the one synchronous call we don't own: **Payment → the external gateway**.

> **Two different failures, easy to conflate — say them by their right names.** "Payment is down" as a whole sentence is ambiguous. A down **Payment service** (the process itself) means Kafka still queues `order-placed` and nothing confirms until it's back up — no breaker involved, that's ADR-025's cross-service resilience, not this ADR. A down **gateway** (the thing Payment calls out to, Razorpay-class) is what this whole day is about: Payment itself is perfectly healthy, Kafka keeps delivering, `PaymentService` keeps running — the breaker/`Payment:OnGatewayUnavailable` lever decide what happens to the *order*, not whether Payment the service is reachable.

## Start the stack
```bash
git checkout day-14
docker compose --profile observability up -d          # Day-13 stack: Grafana :3000, Jaeger :16686, Prometheus :9090
```
```powershell
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"   # before each service
dotnet run --project src/Tadka.Restaurant.Api   # :5260
dotnet run --project src/Tadka.Payment.Api      # :5240
dotnet run --project src/Tadka.Delivery.Api     # :5250
dotnet run --project src/Tadka.Api              # :5224
dotnet run --project src/Tadka.Gateway          # :8080
```
Levers (env on Payment): `Payment__Gateway__Behavior` = `Fast` / `Slow` / `Failing` (business **decline** — final, no retry/breaker) / **`Outage`** (transport failure — retried + trips the breaker) · `Payment__CircuitBreakSeconds` (60; use 15 for a snappy demo) · `Payment__MaxRetryAttempts` (2) · `Payment__TimeoutSeconds` (2) · `Payment__MaxConcurrentCharges` (10). Seeded pwd `Password123!`.

## Demo 1 — Circuit breaker (open → half-open → recover)
```bash
# relaunch Payment with Payment__Gateway__Behavior=Outage + Payment__CircuitBreakSeconds=15; place ~5 orders via :8080
#  log: PaymentGatewayUnavailableException (after jittered retries)
#       "⚡ Payment circuit OPENED for ~15s — gateway looks down; failing fast."
#       then BrokenCircuitException per order — fail-fast in ms, ZERO gateway calls
#  after 15s: "🔁 Payment circuit HALF-OPEN — probing…" → probe fails → re-OPENED
# restore Behavior=Fast → next order: "💳 Payment COMPLETED" (saga confirms, ₹698)
# Prometheus: tadka_payment_circuit_transitions_total{state="open"|"half_open"}
# CONTRAST: Behavior=Failing (a decline) — no retry, no breaker trip. Decline ≠ outage.
```

## Demo 1b — Compensate vs Buffer (fix 2 / ADR-043), same outage, two endings
```bash
# Run 1: Payment__OnGatewayUnavailable=Compensate (default) + Behavior=Outage; place ~5 orders via :8080
#   breaker opens, all ~5 orders end Failed/Cancelled — permanent, even after the gateway recovers
# Restart Payment with Payment__OnGatewayUnavailable=Buffer, same Behavior=Outage; place the same ~5 orders
#   NO Payment row is created for any of them (no ghost Pending row — see PaymentService.ChargeAsync)
#   log: "⏳ Payment BUFFERED for order ... — gateway unavailable, will retry later (Buffer mode)."
#   Kafka consumer-group LAG grows (kafka-consumer-groups.sh) instead of the orders being cancelled
# restore Behavior=Fast → within ~CircuitBreakSeconds, every buffered order confirms: "💳 Payment COMPLETED"
#   lag returns to 0, and NONE of those 5 orders were ever cancelled
```
Compare the order list after each run: Compensate leaves 5 permanently Cancelled orders; Buffer leaves 5 orders that took longer to confirm but never cancelled. Neither is "correct" — it's a product decision, named honestly in ADR-043's trade-off.

## Demo 2 — Redis down → cache fall-through (ADR-018/044)
```bash
docker compose stop redis
# GET a menu via :8080 → still 200 (DB fall-through), measured ~1s warm → ~6.3s with Redis down. No errors.
docker compose start redis
# Lesson: Redis = PERFORMANCE dep. The DB must survive a full cache outage — watch its load panel.
```

## Demo 3 — Hot-key stampede → single-flight lock (ADR-019)
```bash
# hammer ONE menu key at its TTL boundary (parallel GETs as it expires)
# with the single-flight lock (in since Day 6): ~1 DB refresh, the herd waits ~80ms and reads the repopulated key
# watch the DB-QPS panel: spike (no lock) vs flat (lock)
```

## Demo 4 — Slow-not-down (timeout + retry)
```bash
# relaunch Payment with Payment__Gateway__Behavior=Slow (8s gateway); place ONE order
# log: two "💤 SLOW ~8s" attempts ~2.25s apart — the 2s per-attempt timeout abandoned each + jittered retry
# breaker stays CLOSED (1 order < min-throughput 5 — one slow order is not an outage)
```

## What changed
- `src/Tadka.Payment.Api/Resilience/PaymentResiliencePipeline.cs` — pipeline completed: bulkhead → **retry** (jittered exp backoff, transport-only) → **circuit breaker** (ratio 0.5 / min-throughput 5 / 30 s window / 60 s break, knobs in `PaymentOptions`) → 2 s timeout. State transitions logged + emitted as `tadka.payment.circuit_transitions{state}` via `Tadka.Telemetry`.
- `IPaymentGateway` — new `PaymentGatewayUnavailableException` (transport) vs `PaymentDeclinedException` (business); `FakePaymentGateway` — new `Outage` behavior.
- No new code for Demos 2–3: the Day-6 `RedisCacheService` already falls through on `RedisException` and carries the single-flight lock — Day 14 *proves* it under chaos.
- **Fix 2 (`Payment:OnGatewayUnavailable`):** `PaymentOptions.OnGatewayUnavailable` (`Compensate` default | `Buffer`), a new `GatewayUnavailableRetryLaterException`, `PaymentService.ChargeAsync`'s Buffer branch (deletes the Pending row instead of committing a permanent Failed one), and `OrderPlacedConsumer`'s dedicated catch clause (seeks back + backs off ~the breaker's break duration, never counted as a poison attempt). See Demo 1b above and ADR-043.
- `PaymentServiceGatewayUnavailableTests.cs` (pure unit tests, EF Core InMemory, 4/4 passing) validating Compensate mode, Buffer mode, and the ghost-row protection.

## Where a breaker must NOT go
Postgres (core/correctness — can't degrade), the Day-12 pricing read (`LocalReplica` already degrades), or in front of a stale-financial fallback (**never serve stale money**). See ADR-044.

## Reset
`Behavior=Fast`, `docker compose start redis`, or `--profile observability down`.

## Tests
`dotnet test Tadka.slnx` → **187 tests** passing across all services (Api 70, Payment 29, Delivery 18, Restaurant 15, Gateway 15, + domain/architecture suites). The 4 `PaymentServiceGatewayUnavailableTests` are pure unit tests (EF Core InMemory) and pass in ~1 second without Docker.

PgBouncer `:6432` is in compose (Day 11, carried forward) — not a Day-14 teaching beat. Windows: `curl.exe`. Contrast **`Outage`** (trips breaker) vs **`Failing`** (decline, no retry, no trip). Class lever: `Payment__CircuitBreakSeconds=15`. Admission/shed: `Backpressure:MaxConcurrent`, `LoadShed:Enabled`.
