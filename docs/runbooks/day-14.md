# Runbook — Day 14: Resilience & Chaos Engineering

Day 13 made the system observable; Day 14 makes it **survivable** — and measures every failure on the Day-13 dashboards. The circuit breaker + retry complete the Payment→gateway pipeline (ADR-043); dependency classification + graceful degradation are the policy (ADR-044).

> **The reframe:** the Ordering→Payment hop is Kafka (Day 9) — a down Payment just makes messages wait. The breaker lives on the one synchronous call we don't own: **Payment → the external gateway**.

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

## Where a breaker must NOT go
Postgres (core/correctness — can't degrade), the Day-12 pricing read (`LocalReplica` already degrades), or in front of a stale-financial fallback (**never serve stale money**). See ADR-044.

## Reset
`Behavior=Fast`, `docker compose start redis`, or `--profile observability down`.

## Tests
`dotnet test` → **43/43** (breaker min-throughput ≥ test volume; retry transport-only — happy path untouched).
