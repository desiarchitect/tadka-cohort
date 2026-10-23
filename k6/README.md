# Tadka: k6 Load-Test Suite (Day 16)

> **Read the before/after numbers, not the code.** A load test deliverable is a measured number: p99 at the knee, the VU count where it broke, the resource that fell first, and the decision you make because of it. The script is just the fixture that produces the number.

The suite drives the **canonical "4 services + gateway"** system through the YARP gateway on `:8080`. One request flow lives in [`lib.js`](lib.js) (list → menu → maybe-order); each profile below differs **only** in its VU-vs-time shape, which is exactly what each load-test *type* is built to isolate.

## The profiles

| File | Shape | Question it answers | Watch |
|------|-------|---------------------|-------|
| [`smoke.js`](smoke.js) | 1 VU, 30s | Is it functional at all? (sanity, not load) | All green = go |
| [`average-load.js`](average-load.js) | ramp → ~50 VUs → hold → down | Does the SLO hold under everyday traffic? | menu/list p99 < 300ms |
| [`stress.js`](stress.js) | climb until it breaks | **Where is the breaking point?** | the knee + first resource to saturate |
| [`spike.js`](spike.js) | quiet → instant flood → drop → recover | Does it survive a sudden surge and recover? | p99 returns to baseline (the breaker opens only if you also start a Payment outage, see Day 14) |
| [`dinner-rush.js`](dinner-rush.js) | Week-2 concurrency profile (monolith-direct `:5224`) | Does concurrency break naive code? | the Week-2 break-kit before/after |
| [`hot-key.js`](hot-key.js) | sustained VUs, **~80%** traffic to Meghana menu | Does a celebrity restaurant melt one cache key or DB? | menu p99 spike; pair with stampede toy + single-flight |
| [`polling-vs-sse.js`](polling-vs-sse.js) | N users polling `GET /orders/{id}` once a second vs N open SSE streams (needs `-e ORDER_ID` and `-e ORDER_TOKEN`) | What does polling cost compared with a push stream? | total request count: polling grows with users x seconds, SSE with users only |
| [`evolution.js`](evolution.js) | **open model**: requests per second in steps (scenarios `browse`, `history`, `hotkey`, `orders`), same workload on every checkpoint branch | What did each day's change buy? | capacity per checkpoint; normally run by `evolution/run-checkpoint.ps1` |
| [`lib.js`](lib.js) | not a test: the shared session, SLO thresholds and counters | | |

> `dinner-rush.js` is the original **Week-2 break-kit** fixture and deliberately targets the monolith directly (`:5224`). Its order step needs `-e ORDER_CUSTOMER_ID` and `-e ORDER_TOKEN` (orders need a JWT since Day 10), and the monolith must run with `RateLimit__PerMinute=100000`. The Day-16 profiles target the gateway (`:8080`). The runbook [`docs/runbooks/day-16.md`](../docs/runbooks/day-16.md) describes every script in detail.

---

## 0. Prerequisites & Rate Limit Configuration

### Install k6

**Windows (PowerShell):**
```powershell
winget install GrafanaLabs.k6
```

**macOS (Homebrew):**
```bash
brew install k6
```

**Linux (Debian/Ubuntu):**
```bash
sudo gpg -k
sudo gpg --no-default-keyring --keyring /usr/share/keyrings/k6-archive-keyring.gpg --keyserver hkp://keyserver.ubuntu.com:80 --recv-keys C5AD17C747E3415A3642D57D77C6C491D6AC1D69
echo "deb [signed-by=/usr/share/keyrings/k6-archive-keyring.gpg] https://dl.k6.io/deb stable main" | sudo tee /etc/apt/sources.list.d/k6.list
sudo apt-get update && sudo apt-get install k6
```

### CRITICAL: Rate Limiting vs Capacity (The 429 Trap)

The YARP Gateway enforces edge rate limiting (default 120 req/min per IP, ADR-035), and the monolith enforces distributed rate limiting (default 120 req/min, ADR-049).

Because all k6 Virtual Users on a local machine share a single IP (`127.0.0.1`), hitting the default limits triggers HTTP `429 Too Many Requests` after ~120 requests, creating a **false knee** that represents a client rate limiter rather than backend system capacity.

Before running load tests, start the Gateway and Monolith with rate limits raised:

**PowerShell:**
```powershell
$env:Gateway__RateLimitPerMinute = "100000"
$env:RateLimit__PerMinute = "100000"
```

**Bash:**
```bash
export Gateway__RateLimitPerMinute=100000
export RateLimit__PerMinute=100000
```

`lib.js` tracks a `rate_limited` Counter. If any 429 response occurs, the test threshold immediately fails with the alert message: `rate limiter, not capacity: raise Gateway__RateLimitPerMinute and RateLimit__PerMinute`.

---

## 1. Running the Tests

Bring the system up with the observability profile:

**Bash:**
```bash
docker compose --profile observability up -d
```

**PowerShell:**
```powershell
docker compose --profile observability up -d
```

### 1) Always Smoke First
Sanity check that all endpoints route and return 200:

**Bash:**
```bash
k6 run k6/smoke.js
```

**PowerShell:**
```powershell
k6 run k6/smoke.js
```

### 2) Prove the SLO Under Average Load
Ramp to 50 concurrent users to verify menu/list P99 < 300 ms and errors < 1%:

**Bash:**
```bash
k6 run k6/average-load.js
```

**PowerShell:**
```powershell
k6 run k6/average-load.js
```

### 3) Find the Breaking Point (Stress Test)
Climb concurrency to discover the saturation point:

**Bash:**
```bash
k6 run k6/stress.js
# Or push higher:
k6 run -e MAX_VUS=400 k6/stress.js
```

**PowerShell:**
```powershell
k6 run k6/stress.js
# Or push higher:
k6 run -e MAX_VUS=400 k6/stress.js
```

### 4) Sudden Surge & Recovery (Spike Test)
Test survival under immediate traffic flood and verify recovery:

**Bash:**
```bash
k6 run k6/spike.js
```

**PowerShell:**
```powershell
k6 run k6/spike.js
```

### 5) Hot-Key / Stampede Test
Invalidate Meghana menu key in Redis and flood with traffic:

**Bash:**
```bash
docker exec tadka-redis redis-cli DEL restaurant:a1b2c3d4-0001-4000-8000-000000000001:menu
k6 run k6/hot-key.js
```

**PowerShell:**
```powershell
docker exec tadka-redis redis-cli DEL restaurant:a1b2c3d4-0001-4000-8000-000000000001:menu
k6 run k6/hot-key.js
```

---

## 2. Environment Variables

| Var | Default | Meaning |
|-----|---------|---------|
| `BASE_URL` | `http://localhost:8080` | Target gateway. Set to `http://localhost:5224` to hit monolith directly. |
| `PEAK_VUS` | `50` (average), `80` (hot-key) | Sustained concurrency for average-load and hot-key profiles. |
| `MAX_VUS` | `300` (stress) | Top of the stress ramp. |
| `SPIKE_VUS` | `200` (spike) | Flood level for the spike profile. |
| `HOT_SHARE` | `0.80` (hot-key) | Fraction of requests targeting the hot restaurant (Meghana). |
| `ORDER_CUSTOMER_ID` | _(unset)_ | Seeded customer ID (`c1b2c3d4-0001-4000-8000-000000000001` for Priya) for write paths. |
| `ORDER_TOKEN` | _(unset)_ | JWT from `POST /api/v1/auth/login` for authenticated order creation. |
| `ORDER_SHARE` | `0.15` | Fraction of sessions that place an order (default 15%). |
| `FDID` | _(unset)_ | Azure Front Door ID header (`X-Azure-FDID`) when bypassing CDN in cloud tests. |

---

## 3. Obtaining a Token for Write Tests

To test the order write path:

**Bash:**
```bash
TOKEN=$(curl -s -X POST http://localhost:8080/api/v1/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"priya@tadka.test","password":"Password123!"}' | jq -r .accessToken)

k6 run -e ORDER_CUSTOMER_ID=c1b2c3d4-0001-4000-8000-000000000001 -e ORDER_TOKEN=$TOKEN k6/average-load.js
```

**PowerShell:**
```powershell
# Invoke-RestMethod, not curl.exe -d: Windows PowerShell 5.1 strips the quotes from the JSON body when it is passed to curl.exe.
$res = Invoke-RestMethod -Uri http://localhost:8080/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"priya@tadka.test","password":"Password123!"}'

$env:ORDER_CUSTOMER_ID = "c1b2c3d4-0001-4000-8000-000000000001"
$env:ORDER_TOKEN = $res.accessToken

k6 run k6/average-load.js
```

---

## 4. Reading the Result

The k6 terminal summary reports P50, P95, P99, error rates, and requests/sec. The architectural story lives on the Day-13 dashboards:

- **Grafana RED dashboard** (`http://localhost:3000`): Rate, Errors, Duration per service. Where does P99 knee up? Which service saturates first?
- **`tadka_payment_circuit_transitions_total`** (Prometheus / Grafana): Does the circuit breaker transition to OPEN during payment outage, and close on recovery?
- **Jaeger** (`http://localhost:16686`): Open a slow trace at the knee. Which span owns the dominant latency?
- **Postgres**: Inspect active connections and cache hits via `pg_stat_activity` and `pg_stat_database`.

At **1 lakh orders/day (~25/s peak)**, a single box handles the traffic easily. The architecture exists because the business grew into distinct ownership, data, and resilience requirements.
