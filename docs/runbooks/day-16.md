# Runbook — Day 16: Load Testing, Cost & Portfolio (the finale)

The system is built. Day 16 doesn't add a pattern — it **measures** the system,
puts a ₹ number on it, and packages it. The headline demo is the **k6 stress run
→ breaking point**, read live on the Day-13 dashboards. No app code changes; no
new ADR (canon stays at 044). Tests stay **43/43**.

> The earned failure: "it works with curl and 5 users — does it survive the IPL
> final?" You don't know your breaking point until you measure it.

## Start the stack
```bash
git checkout day-16
docker compose --profile observability up -d   # core + Grafana :3000, Jaeger :16686, Prometheus :9090
```
```powershell
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"   # before each service
dotnet run --project src/Tadka.Restaurant.Api   # :5260
dotnet run --project src/Tadka.Payment.Api      # :5240
dotnet run --project src/Tadka.Delivery.Api     # :5250
dotnet run --project src/Tadka.Api              # :5224
dotnet run --project src/Tadka.Gateway          # :8080  ← load tests target this
```
Install k6: `winget install k6` (Windows) · `brew install k6` (macOS) · `sudo apt install k6` (Linux).

## Demo — the four-profile arc (read each on Grafana RED + Jaeger)
```bash
# 1) SMOKE — sanity, not load. If this fails you have a BUG, not a scaling problem.
k6 run k6/smoke.js

# 2) AVERAGE — weekday-lunch crowd (~50 VUs). The SLO should HOLD:
#    menu/list p99 < 300ms, <1% errors. Watch the Grafana RED dashboard stay green.
k6 run k6/average-load.js

# 3) STRESS — climb until it breaks. THE HEADLINE.
#    Capture: the VU count where p99 knees up, and which resource saturates FIRST
#    (Npgsql connection pool / Kafka consumer lag / container memory).
k6 run k6/stress.js          # push harder with -e MAX_VUS=400

# 4) SPIKE — instant flood → drop → recovery. Two questions:
#    (a) survival: does the Day-14 breaker go OPEN (Prometheus
#        tadka_payment_circuit_transitions_total{state="open"}) and fail fast?
#    (b) recovery: does p99 return to baseline after the spike, or stay degraded
#        (a leak: pool not released / Kafka lag never drains / Redis mem held)?
k6 run k6/spike.js
```

### Exercise the write path (optional)
```bash
# get a JWT (seeded admin, pwd Password123!)
curl -s -X POST http://localhost:8080/api/v1/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@tadka.local","password":"Password123!"}'
# then pass the token + a seeded customer id:
k6 run -e ORDER_TOKEN=<jwt> -e ORDER_CUSTOMER_ID=<seeded-id> k6/average-load.js
```
Without these, browse reads (list + menu, anonymous + Redis-cached) still drive
the hot paths — which is the concurrency that breaks first at 1 lakh/day.

## Reading the result
- **k6 summary (terminal):** p50/p95/p99, error rate, req/s — the raw numbers.
- **Grafana RED dashboard (:3000):** where p99 knees up; which service reddens first.
- **`tadka_payment_circuit_transitions_total{state}` (Prometheus/Grafana):** breaker open→half-open→closed under the spike.
- **Jaeger (:16686):** open a slow trace at the knee; the span owning the latency is the bottleneck (usually DB time once the pool drains).

## The capstone framing (not a number — a judgment)
At **1 lakh orders/day (~25/s peak)** you are nowhere near the breaking point — a
single box would do. The 4-services-+-gateway architecture exists because the
*business* grew into it (see [`tadka-growth-story.md`](../tadka-growth-story.md)),
and the load test lets you point at the exact concurrency where each next move
(scale up → scale out → cache harder → shard) becomes justified. Cost of it all:
[`cost-model.md`](../cost-model.md). The whole system, explained:
[`architecture.md`](../architecture.md).

## What changed (docs + k6 only — no app code)
- `k6/lib.js` (shared flow), `k6/smoke.js`, `k6/average-load.js`, `k6/stress.js`, `k6/spike.js`, `k6/README.md`. `k6/dinner-rush.js` kept (Week-2 break-kit, monolith-direct).
- `docs/cost-model.md`, `docs/architecture.md`, this runbook.

## Reset
`docker compose --profile observability down`. (k6 runs from the host; nothing to clean up.)

## Tests
`dotnet test` → **43/43** (no app code touched).
