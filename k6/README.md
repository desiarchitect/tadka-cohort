# Tadka — k6 Load-Test Suite (Day 16)

> **Read the before/after numbers, not the code.** A load test's deliverable is a
> measured number — p99 at the knee, the VU count where it broke, the resource
> that fell first — and the decision you make because of it. The script is just
> the fixture that produces the number.

The suite drives the **canonical "4 services + gateway"** system through the YARP
gateway on `:8080`. One request flow lives in [`lib.js`](lib.js) (list → menu →
maybe-order); each profile below differs **only** in its VU-vs-time shape — which
is exactly what each load-test *type* is built to isolate.

## The profiles

| File | Shape | Question it answers | Watch |
|------|-------|---------------------|-------|
| [`smoke.js`](smoke.js) | 1 VU, 30s | Is it functional at all? (sanity, not load) | All green = go |
| [`average-load.js`](average-load.js) | ramp → ~50 VUs → hold → down | Does the SLO hold under everyday traffic? | menu/list p99 < 300ms |
| [`stress.js`](stress.js) | climb until it breaks | **Where is the breaking point?** | the knee + first resource to saturate |
| [`spike.js`](spike.js) | quiet → instant flood → drop → recover | Does it survive a sudden surge *and* recover? | circuit breaker OPEN; p99 returns to baseline |
| [`dinner-rush.js`](dinner-rush.js) | Week-2 concurrency profile (monolith-direct `:5224`) | Does concurrency break naive code? | the Week-2 break-kit before/after |
| [`hot-key.js`](hot-key.js) | sustained VUs, **~80%** traffic to Meghana menu | Does a celebrity restaurant melt one cache key / DB? | menu p99 spike; pair with stampede toy + single-flight |

> `dinner-rush.js` is the original **Week-2 break-kit** fixture and deliberately
> targets the monolith directly (`:5224`) — keep it as-is; the four Day-16
> profiles target the gateway (`:8080`).

## Run it

```bash
# 0) Install k6
#    Windows: winget install k6     macOS: brew install k6     Linux: sudo apt install k6

# 1) Bring the system up WITH the observability stack (so you can read the result)
docker compose --profile observability up -d
#    + run the 4 services (monolith :5224, payment :5240, delivery :5250, restaurant :5260)
#      and the gateway :8080 (see docs/runbooks/day-16.md for the exact commands)

# 2) Always smoke first
k6 run k6/smoke.js

# 3) Prove the SLO under normal load
k6 run k6/average-load.js

# 4) Find the breaking point (the headline)
k6 run k6/stress.js          # or -e MAX_VUS=400 to push further

# 5) Survive + recover from a surge
k6 run k6/spike.js

# 6) Hot-key / celebrity menu (Day 6 + Day 14 stampede)
#    Invalidate Meghana's cache key first, then flood:
#    docker exec tadka-redis redis-cli DEL restaurant:a1b2c3d4-0001-4000-8000-000000000001:menu
k6 run k6/hot-key.js
```

## Env vars

| Var | Default | Meaning |
|-----|---------|---------|
| `BASE_URL` | `http://localhost:8080` | Target (gateway). Set to `http://localhost:5224` to hit the monolith directly. |
| `PEAK_VUS` | `50` (average) | Sustained concurrency for the average-load profile. |
| `MAX_VUS` | `300` (stress) | Top of the stress ramp. |
| `SPIKE_VUS` | `200` (spike) | Flood level for the spike. |
| `ORDER_CUSTOMER_ID` | _(unset)_ | A seeded customer id — **opt-in** for the write path. |
| `ORDER_TOKEN` | _(unset)_ | A JWT from `POST /api/v1/auth/login` (seeded user, pwd `Password123!`) — required for order writes (Day 10). |
| `ORDER_SHARE` | `0.15` | Fraction of sessions that place an order. |

Browse reads are anonymous (Restaurant service GETs are public + Redis-cached).
Without `ORDER_CUSTOMER_ID`/`ORDER_TOKEN` a run still drives the **hot read
paths** — which is the concurrency that actually breaks first at 1 lakh/day.

### Get a token for the write path

```bash
# returns { "token": "..." } — paste it into ORDER_TOKEN
curl -s -X POST http://localhost:8080/api/v1/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@tadka.local","password":"Password123!"}'
```

## Reading the result

The k6 terminal summary gives you p50/p95/p99, error rate, and req/s. The
**story** is on the Day-13 dashboards:

- **Grafana RED dashboard** — Rate, Errors, Duration per service. Where does p99
  knee up? Which service goes red first?
- **`tadka.payment.circuit_transitions{state}`** (Grafana, Day 14) — does the
  breaker go OPEN under the spike, then HALF-OPEN → closed on recovery?
- **Jaeger** — open a slow trace at the knee; the span that owns the latency is
  your bottleneck (usually DB time once the connection pool drains).

The honest capstone lesson: at **1 lakh orders/day (~25/s peak)** you are nowhere
near the breaking point — a single box handles it. The architecture exists
because the *business* grew into it, and now you can point at the exact load
where each next move (scale up → scale out → cache harder → shard) becomes
justified. See [`docs/cost-model.md`](../docs/cost-model.md).
