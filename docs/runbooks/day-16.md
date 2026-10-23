# Day 16: Runbook: Load Testing, Observability, Cost Modeling & Cloud Deployment

**Branch:** `day-16` · **What changed since Day 15:** [`docs/changelog.md`](../changelog.md).

Day 15 organized our architectural evidence for the senior system design interview. Day 16 is the **production capstone milestone**. On Day 16, we measure everything we built. We push the full 4-service system to its knee with k6 load tests, trace distributed bottlenecks across OpenTelemetry and Jaeger, compute the exact unit economics (rupees per 1,000 orders), and walk through the live Azure cloud deployment.

> **The Core Architectural Mindset:** An architect does not guess capacity or cost. An architect measures the breaking point, knows which bottleneck breaks first, and calculates the exact rupee cost of every architectural boundary.

Commands are given in Bash and PowerShell where both make sense. Every PowerShell command here runs in Windows PowerShell 5.1 (the one that ships with Windows) and in PowerShell 7. The one exception is the evolution harness in section 4, which needs PowerShell 7 (`pwsh`). Bash steps use `jq` (`sudo apt install jq` / `brew install jq`) to read the token.

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

Pre-create the eight Kafka topics this branch uses (the broker uses SASL, so the CLI needs the client config). Each has one partition, which matters in section 3:

**Bash:**
```bash
for t in order-placed payment-results order-confirmed delivery-assigned refund-requested payment-refunded menu-updated restaurant-response; do
  docker exec tadka-kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --command-config /etc/kafka/docker/client.properties --create --if-not-exists --topic $t --partitions 1 --replication-factor 1
done
```

**PowerShell:**
```powershell
foreach ($t in "order-placed","payment-results","order-confirmed","delivery-assigned","refund-requested","payment-refunded","menu-updated","restaurant-response") {
  docker exec tadka-kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --command-config /etc/kafka/docker/client.properties --create --if-not-exists --topic $t --partitions 1 --replication-factor 1
}
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
# Invoke-RestMethod, not curl.exe -d: Windows PowerShell 5.1 strips the quotes from a JSON string passed to curl.exe.
$login = Invoke-RestMethod -Uri http://localhost:8080/api/v1/auth/login -Method Post -ContentType "application/json" -Body '{"email":"priya@tadka.test","password":"Password123!"}'
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

The access token lives 15 minutes (900s). A stress run takes 8 minutes and a spike run about 4, so **re-run the login before each of them**, then rebuild `$orderArgs` / `ORDER_ARGS` so they carry the new token. An expired token does not fail the run: every order request returns 401, `orders_placed` stays 0 and the browse numbers still look fine. About 15% of sessions place an order (`ORDER_SHARE`).

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

Expected result: all checks pass (30 of 30 in a 30 second run), `rate_limited` 0, `browse_errors` 0%, thresholds green. Smoke runs only about 15 sessions and 15% of them order, so `orders_placed` is often 0 here. That is normal. The next test places hundreds.

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

Ramps traffic up to 300 VUs (`-e MAX_VUS=...` to change it) and watches for the knee:

**Bash:**
```bash
k6 run $ORDER_ARGS k6/stress.js
```

**PowerShell:**
```powershell
k6 run @orderArgs k6/stress.js
```

Observe the knee: at lower concurrency, latency is flat. Once connection pools or CPU saturate, queue wait time grows non-linearly. Note the VU count where p99 crosses the 300 ms SLO and where errors first appear (the run itself only flags the catastrophic end: p99 > 5 s or > 25% errors). That number and the first resource to saturate are the deliverable.

**On a laptop, 300 VUs does not reach the knee.** Each VU thinks for 1 to 3 seconds between requests, so 300 VUs is only about 150 requests per second. See "What a laptop run looks like" below: latency stayed flat the whole way and nothing failed. That is a real result for the HTTP path (large headroom over the design load of about 1.2 orders per second, 25 at burst), but it is not the knee. **The knee is somewhere else: in the queue.** `POST /orders` returns `201` as soon as the order and its outbox row are saved, so HTTP latency stays flat even when payments fall behind. Payment works through `order-placed` one message at a time (one partition, one consumer, about 200 ms per fake charge), which is about **4 orders per second**. Send orders faster than that and the consumer group `tadka-payment` builds a lag, and new orders stay `Created` until Payment catches up. After the average, stress and spike runs back to back the lag was over 1,000 messages, and at 4 orders per second it took several minutes to drain. Watch it while the test runs (Surface 4 below). That lag, not p99, is the first thing to saturate here, and it is the argument for more partitions and more Payment consumers.

To push the HTTP side too, raise the ceiling, for example `-e MAX_VUS=1500`, and watch k6 itself: once k6 uses a full CPU core, the machine you are measuring has become the bottleneck, not Tadka. We have not measured where the HTTP path breaks on a laptop, so find your own number before class and write it down.

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

This profile does **not** open the Payment circuit breaker by itself. The breaker sits on the call from Payment to the payment gateway, and the fake gateway answers fast, so a flood of customers never trips it. To see it open during a spike, run the Payment outage from [Day 14, Demo 1](day-14.md) while the spike is running. Without that, expect `tadka_payment_circuit_transitions_total` to stay empty.

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

### What a laptop run looks like

Measured on a developer laptop with the five services started with `dotnet run` (as in section 1), the Docker stack from section 1, and the rate limits raised. Use these as a rehearsal check: your numbers will differ, but they should look like this and `rate_limited` must be 0.

| Test | Load | Requests per second | Menu p99 | Errors | Orders placed |
|---|---|---|---|---|---|
| Smoke | 1 VU, 30 s | about 1 | p95 60 ms (first calls are cold) | 0 | usually 0 |
| Average load | 50 VUs, 5 min | about 43 | 6.6 ms | 0 | 875 |
| Stress | up to 300 VUs, 8 min | about 150 | 5.5 ms | 0 | 5,008 |
| Spike | 200 VUs, 4 min 20 s | about 64 | 5.3 ms | 0 | 1,170 |
| Hot key | 80 VUs, 4 min | about 166 | 5.1 ms (hot menu) | 0 | none |

The list endpoint p99 in the average run was 10.6 ms. All thresholds passed and `rate_limited` was 0 in every run.

"Orders placed" counts `201` responses, not confirmed orders. Payment confirmed only about 4.4 orders per second, so after the runs above the `tadka-payment` lag was over 1,000 and the newest orders were still `Created`. Check the lag (Surface 4) before you start the next test, and let it drain, or the next run starts with a backlog.

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

The gateway names its spans after the route pattern, so there is no operation called `POST /api/v1/orders`. The easiest way to find an order trace is to select service `Tadka.Payment.Api`, operation `ProcessPayment`, and click Find Traces. Every row there is one order, and a full order trace has about 13 spans across `Tadka.Gateway`, `Tadka.Api`, `Tadka.Payment.Api`, `Tadka.Delivery.Api` and `Tadka.Restaurant.Api`. (If you search `Tadka.Gateway` instead, the newest traces during a k6 run are browse reads with 3 spans: pick the operation `POST /api/v1/{**remainder}` to get order writes.) Click a trace to observe the distributed span:

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

### Surface 4: Kafka consumer lag (where the saga saturates first)

Open [http://localhost:8090](http://localhost:8090) (Kafka UI), then Consumers, then `tadka-payment`. Or read it from the CLI:

**Bash:**
```bash
docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --command-config /etc/kafka/docker/client.properties --describe --group tadka-payment
```

**PowerShell:**
```powershell
$env:MSYS_NO_PATHCONV = "1"   # only needed in Git Bash
docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server localhost:9092 --command-config /etc/kafka/docker/client.properties --describe --group tadka-payment
```

The `LAG` column on `order-placed` is the number of orders waiting for a charge. Near 0 means Payment keeps up. A number that keeps climbing during the test means orders are arriving faster than Payment can charge them. Reading an order's status then shows `Created` instead of `Confirmed`.

---

### Prove the journey: the same test on every checkpoint

The tests above measure the finished system. To show what each day *bought*, run the same workload against six checkpoints (`day-03`, `day-05`, `day-06`, `day-07`, `day-09`, `day-16`) on the same 200k-order data set. Each checkpoint gets the pressure that day was built for: a big table, a hot key, a slow or dead Payment, more traffic than one replica can take.

It needs **PowerShell 7** (`winget install Microsoft.PowerShell`, then run `pwsh`; Windows PowerShell 5.1 is not supported) and it starts its own copies of the stack, so **stop your own Tadka services and containers first**. Add `-Quick` for a class-length rehearsal (about 2 minutes per checkpoint, not numbers to quote).

```powershell
pwsh evolution/run-ladder.ps1        # about 2.5 hours; then open evolution/report.md
pwsh evolution/run-ladder.ps1 -Quick # rehearsal
```

The full method, the expected results and the "1 user → 1 million users" model are in [evolution-1-to-1m.md](evolution-1-to-1m.md).

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
