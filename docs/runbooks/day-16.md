# Day 16: Runbook: Load Testing, Observability, Cost Modeling & Cloud Deployment

**Branch:** `day-16` · **What changed since Day 15:** [`docs/changelog.md`](../changelog.md).

Day 15 organized our architectural evidence for the senior system design interview. Day 16 is the **production capstone milestone**. On Day 16, we measure everything we built. We push the full 4-service system to its knee with k6 load tests, trace distributed bottlenecks across OpenTelemetry and Jaeger, compute the exact unit economics (rupees per 1,000 orders), and walk through the live Azure cloud deployment.

> **The Core Architectural Mindset:** An architect does not guess capacity or cost. An architect measures the breaking point, knows which bottleneck breaks first, and calculates the exact rupee cost of every architectural boundary.

Commands are given in Bash and PowerShell where both make sense. The five service terminals, the `psql` check and the cloud scripts (`scripts/*.ps1`) are PowerShell (`pwsh`). Bash steps use `jq` (`sudo apt install jq` / `brew install jq`) to read the token.

---

## 1. Prerequisites: Infrastructure and Services

Start the core infrastructure along with the observability profile:

**Bash:**
```bash
docker compose --profile observability up -d
until docker inspect tadka-kafka --format "{{.State.Health.Status}}" | grep -q healthy; do sleep 3; done
until docker inspect tadka-restaurant-db --format "{{.State.Health.Status}}" | grep -q healthy; do sleep 3; done
docker compose ps
```

**PowerShell:**
```powershell
docker compose --profile observability up -d
do { Start-Sleep -Seconds 3 } until ((docker inspect tadka-kafka --format "{{.State.Health.Status}}") -eq "healthy")
do { Start-Sleep -Seconds 3 } until ((docker inspect tadka-restaurant-db --format "{{.State.Health.Status}}") -eq "healthy")
docker compose ps
```

Build the entire solution:

```bash
dotnet build Tadka.slnx
```

### Critical Environment Setting for Local Load Testing

In local environments, all virtual users originate from `127.0.0.1`. The production default rate limit is 120 requests per minute per IP. Under k6 load testing, sharing that single loopback address would immediately trigger false 429 Too Many Requests errors.

To measure true system capacity rather than edge throttling, raise the per-minute limit on both the gateway and the monolith to `100000` before starting services:

1. `Gateway__RateLimitPerMinute=100000`
2. `RateLimit__PerMinute=100000`
3. `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`

Start each service in a separate terminal:

**Terminal 1 (Monolith / Ordering + Identity on :5224):**
```powershell
$env:RateLimit__PerMinute = "100000"
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
dotnet run --project src/Tadka.Api
```

**Terminal 2 (Payment Service on :5240):**
```powershell
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
dotnet run --project src/Tadka.Payment.Api
```

**Terminal 3 (Delivery Service on :5250):**
```powershell
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
dotnet run --project src/Tadka.Delivery.Api
```

**Terminal 4 (Restaurant Service on :5260):**
```powershell
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
dotnet run --project src/Tadka.Restaurant.Api
```

**Terminal 5 (YARP API Gateway on :8080):**
```powershell
$env:Gateway__RateLimitPerMinute = "100000"
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
dotnet run --project src/Tadka.Gateway
```

---

## 2. Authentication: Obtaining Priya's Token

Authenticated write endpoints require a valid customer JWT token. We use seeded customer Priya Sharma (`priya@tadka.test`, password `Password123!`, ID `c1b2c3d4-0001-4000-8000-000000000001`).

Retrieve the token:

**Bash:**
```bash
TOKEN=$(curl -s -X POST http://localhost:8080/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"priya@tadka.test","password":"Password123!"}' | jq -r '.accessToken')
echo "Token: $TOKEN"
export TOKEN
```

**PowerShell:**
```powershell
$login = curl.exe -s -X POST http://localhost:8080/api/v1/auth/login `
  -H "Content-Type: application/json" `
  -d '{"email":"priya@tadka.test","password":"Password123!"}' | ConvertFrom-Json
$env:TOKEN = $login.accessToken
Write-Host "Token: $env:TOKEN"
```

k6 needs the token and the customer id to exercise the **order write path**. Without them a run only drives the browse reads and `orders_placed` stays 0. Pass them with `-e` on every `k6 run`:

**Bash:**
```bash
export ORDER_ARGS="-e ORDER_TOKEN=$TOKEN -e ORDER_CUSTOMER_ID=c1b2c3d4-0001-4000-8000-000000000001"
```

**PowerShell:**
```powershell
$orderArgs = @("-e", "ORDER_TOKEN=$env:TOKEN", "-e", "ORDER_CUSTOMER_ID=c1b2c3d4-0001-4000-8000-000000000001")
```

The access token lives 15 minutes (900s), so re-run the login before a long stress or spike run. About 15% of sessions place an order (`ORDER_SHARE`).

Verify the token against the gateway:

**Bash:**
```bash
curl -i http://localhost:8080/api/v1/orders \
  -H "Authorization: Bearer $TOKEN"
```

**PowerShell:**
```powershell
curl.exe -i http://localhost:8080/api/v1/orders `
  -H "Authorization: Bearer $env:TOKEN"
```

---

## 3. The 5 k6 Load Tests

Install k6 if not present: `winget install GrafanaLabs.k6`.

| Test Script | Profile | Target / Purpose |
|---|---|---|
| `k6/smoke.js` | 1 VU, 30 seconds | Sanity check: is the system functional at all? |
| `k6/average-load.js` | ramp 1 min to 50 VUs, hold 3 min, ramp down 1 min | Dinner rush baseline: the SLO must hold (menu/list p99 < 300 ms, < 1% errors) |
| `k6/stress.js` | ramp in 5 stages up to 300 VUs (`MAX_VUS`) over 8 min | Find the knee: where p99 explodes and which resource saturates first |
| `k6/spike.js` | 10 VUs, then 200 VUs in 10 s, hold 1 min, drop, 2 min recovery | IPL flash-sale burst: survival and recovery |
| `k6/hot-key.js` | up to 80 VUs, 80% of traffic to one menu | Cache stampede test: Redis single-flight lock |

The SLO (menu and list `p(99) < 300 ms`, `browse_errors < 1%`) is asserted by smoke and average-load. Stress and spike use a deliberately relaxed `p(99) < 5000 ms`: there a breached threshold is the finding, not a failure. Every profile also fails if any 429 is seen (`rate_limited`), because that means you measured the rate limiter, not capacity.

### Test 1: Smoke Test (Sanity)

Run the smoke test to confirm routes and headers function properly:

**Bash:**
```bash
k6 run $ORDER_ARGS k6/smoke.js
```

**PowerShell:**
```powershell
k6 run @orderArgs k6/smoke.js
```

Expected result: all checks pass, `rate_limited` 0, `browse_errors` 0%, thresholds green, and `orders_placed` > 0 when the order variables are set (with 1 VU and 15% of sessions ordering, run it long enough to see at least one).

### Test 2: Average Load Test (Dinner Rush)

Simulates standard peak load (50 concurrent virtual users):

**Bash:**
```bash
k6 run $ORDER_ARGS k6/average-load.js
```

**PowerShell:**
```powershell
k6 run @orderArgs k6/average-load.js
```

Verify the thresholds pass: menu and list p99 under 300 ms and under 1% errors.

### Test 3: Stress Test (Finding the Knee)

Ramps traffic up to 300 VUs (`-e MAX_VUS=...` to change it) to determine where the local system reaches saturation:

**Bash:**
```bash
k6 run $ORDER_ARGS k6/stress.js
```

**PowerShell:**
```powershell
k6 run @orderArgs k6/stress.js
```

Observe the knee: at lower concurrency, latency is flat. Once connection pools or CPU saturate, queue wait time grows non-linearly. Note the VU count where p99 crosses the 300 ms SLO and where errors first appear (the run itself only flags the catastrophic end: p99 > 5 s or > 25% errors). That number and the first resource to saturate are the deliverable.

### Test 4: Spike Test (Flash Sale)

Simulates sudden traffic surges:

**Bash:**
```bash
k6 run $ORDER_ARGS k6/spike.js
```

**PowerShell:**
```powershell
k6 run @orderArgs k6/spike.js
```

Verify that the system absorbs the spike without dropping connections and recovers to normal latency once the burst subsides.

### Test 5: Hot-Key Test (Meghana Biryani Stampede)

Simulates 80 percent of requests targeting a single popular menu item:

**Bash:**
```bash
k6 run $ORDER_ARGS k6/hot-key.js
```

**PowerShell:**
```powershell
k6 run @orderArgs k6/hot-key.js
```

Redis cache-aside with the single-flight stampede lock (ADR-019) ensures only one request fetches from PostgreSQL while concurrent requests await the cached result.

---

## 4. Observability Under Load

While load tests execute, observe system health across three telemetry surfaces:

### Surface 1: Grafana RED Metrics

Navigate to `http://localhost:3000` (credentials: `admin` / `admin`).

1. Rate: Monitor total requests per second across gateway and services.
2. Errors: Ensure HTTP 5xx rate stays at 0 percent.
3. Duration: Watch P50, P95, and P99 latency percentiles in real time.

### Surface 2: Jaeger Distributed Traces

Navigate to `http://localhost:16686`.

Select service `Tadka.Gateway` or `Tadka.Api` and search for recent traces. Click on a `POST /api/v1/orders` trace to observe the distributed span:

1. Client request hits YARP API Gateway (:8080).
2. Gateway proxies to Tadka.Api (:5224).
3. Tadka.Api prices the order from its local menu replica (no call to the Restaurant service).
4. Order entity and Outbox message are written in one database transaction.
5. Outbox relay worker polls and publishes `order-placed` to Kafka.
6. Tadka.Payment.Api consumes event, processes payment, writes Inbox row, and publishes `payment-results`.
7. The full trace spans HTTP, database writes, outbox relay, and Kafka consumers under a single Trace ID.

### Surface 3: PostgreSQL Connection Pool

Inspect active connections during the stress test:

**PowerShell:**
```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT count(*), state FROM pg_stat_activity WHERE datname='tadka' GROUP BY state;"
```

Notice how Npgsql connection pooling keeps active database connections bounded even when k6 concurrency rises toward 300 VUs.

---

## 5. Unit Economics: Cost Modeling (Rupees per 1,000 Orders)

Architecture decisions directly dictate infrastructure bills.

### The Unit Economics Formula

`Cost per 1,000 orders = (Monthly Infrastructure Cost / Monthly Orders) * 1,000`

### Scale Comparisons

1. Honest Minimal Monolith at 1 lakh orders/day (30 lakh orders/month):
   Monthly cost is ~₹6,100 (1 app server, 1 modest Postgres, 1 Redis).
   Cost per 1,000 orders = (₹6,100 / 3,000,000) * 1,000 = **₹2.03 per 1,000 orders**.

2. Distributed 4-Service System at 1 lakh orders/day (30 lakh orders/month):
   Monthly cost is ~₹31,300 (5 containers, 4 databases, read replica, Redis, Kafka, ALB, observability).
   Cost per 1,000 orders = (₹31,300 / 3,000,000) * 1,000 = **₹10.43 per 1,000 orders**.
   The distributed system costs 5x more at low volume due to fixed isolation overhead (4 separate databases and idle Kafka brokers).

3. Distributed 4-Service System at 10 lakh orders/day (3 crore orders/month):
   Monthly cost is ~₹88,000 (scaled compute, larger DB instances, multi-broker Kafka).
   Cost per 1,000 orders = (₹88,000 / 30,000,000) * 1,000 = **₹2.93 per 1,000 orders**.
   Traffic increased 10x while infrastructure cost increased only 2.8x. The distributed architecture scales sub-linearly once volume amortizes the initial isolation floor.

---

## 6. Live Cloud Deployment (Azure Container Apps, ADR-064)

The cohort supports real, automated cloud provisioning using Azure Container Apps.

### Deployment Scope

1. Free Trial / Student accounts: Use `-NoFrontDoor`. Azure Front Door is restricted on Free Trial tiers. The gateway Container App serves as the public ingress directly.
2. Paid Pay-As-You-Go accounts: Full deployment includes Azure Front Door, global CDN edge caching, WAF rate limiting, and origin header locking (`X-Azure-FDID`).

### Instructor Cloud Commands

Deploy the basic environment with automatic teardown safeguard:

**PowerShell:**
```powershell
./scripts/cloud-up.ps1 -Mode basic -AlertEmail instructor@example.com -NoFrontDoor -AutoDownAfterHours 3
```

Verify deployment health and run end-to-end smoke tests:

**PowerShell:**
```powershell
./scripts/cloud-check.ps1
```

Destroy all cloud resources immediately after class:

**PowerShell:**
```powershell
./scripts/cloud-down.ps1
```

Confirm that the script prints "Resource group ... is GONE" to avoid unexpected cloud charges.

---

## 7. Portfolio Capstone Checklist

Use the completed Tadka repository as a senior system design portfolio artifact:

1. Complete Architecture Record: Cite the 66 ADRs (`docs/adrs/001..067`, there is no 046) as documented trade-offs.
2. Architecture Map: Use `docs/architecture.md` as the canonical reference for the 4-service + gateway design.
3. Cost Model: Present `docs/cost-model.md` to defend the business economics of monolithic vs distributed architecture.
4. Working Telemetry: Demonstrate end-to-end trace propagation across HTTP, outbox tables, and Kafka brokers.
5. Empirical Evidence: Defend interview answers using measured numbers from k6 load runs rather than theoretical assumptions.
