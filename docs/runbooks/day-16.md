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

Run the tests in this order, from the repository root. Each test below says **what it tests**, **what you will see**, and **what you do**. Run them one at a time: two k6 runs at once measure each other, not Tadka.

| Test Script | Profile | Target / Purpose | Takes |
|---|---|---|---|
| `k6/smoke.js` | 1 VU, 30 seconds | Sanity check: is the system functional at all? | 30 s |
| `k6/average-load.js` | ramp 1 min to 50 VUs, hold 3 min, ramp down 1 min | Dinner rush baseline: the SLO must hold (menu/list p99 < 300 ms, < 1% errors) | 5 min |
| `k6/stress.js` | ramp in 5 stages up to 300 VUs (`MAX_VUS`) over 8 min | Find the knee: where p99 explodes and which resource saturates first | 8 min |
| `k6/spike.js` | 10 VUs, then 200 VUs in 10 s, hold 1 min, drop, 2 min recovery | IPL flash-sale burst: survival and recovery | 4 min 20 s |
| `k6/hot-key.js` | up to 80 VUs, 80% of traffic to one menu | Cache stampede test: Redis single-flight lock | 4 min |

The SLO (menu and list `p(99) < 300 ms`, `browse_errors < 1%`) is asserted by smoke and average-load. Stress and spike use a deliberately relaxed `p(99) < 5000 ms`: there a breached threshold is the finding, not a failure. Every profile also fails if any 429 is seen (`rate_limited`), because that means you measured the rate limiter, not capacity.

### What one virtual user does

Smoke, average, stress and spike all run the same customer session, so the tests differ only in **how many users run at once and for how long**:

1. `GET /api/v1/restaurants?page=1&pageSize=10`: the restaurant list. Routed by the gateway to the Restaurant service.
2. `GET /api/v1/restaurants/{id}/menu` for one of the three seeded restaurants. This is the hot read, served from Redis after the first call (ADR-018).
3. Think for 1 to 3 seconds, like a customer reading the menu.
4. About 15% of sessions then place an order: `POST /api/v1/orders` through the gateway, with Priya's token, for 2 items from Meghana Foods. This needs the `$orderArgs` / `ORDER_ARGS` from section 2. Without them, the order step is skipped.

A `201` on the order only means the order and its outbox row were saved. Payment, confirmation and rider assignment happen afterwards through Kafka, and **k6 does not wait for them**. That is why section 4, Surface 4 matters.

### How to read the k6 summary

When a run ends, k6 prints a block. These are the lines to read:

| Line | What it means | Good looks like |
|---|---|---|
| `THRESHOLDS` with `✓` or `✗` | The pass or fail rules of this test | all `✓` |
| `checks_succeeded` | Share of `list 200`, `menu 200`, `order 201` checks that passed | 100% |
| `http_req_duration{name:menu}` (`p(99)`) | 99 out of 100 menu reads were faster than this | under 300 ms |
| `browse_errors` | Share of list and menu reads that did not return 200 | 0% |
| `rate_limited` | Number of 429 responses | 0. Anything else means the rate limit env vars from section 1 are missing |
| `orders_placed` | Orders that returned 201 | more than 0 when the token is set |
| `http_reqs` | Total requests and requests per second | grows with the number of VUs |
| `vus_max` | Most users running at the same moment | matches the profile |

### Test 1: Smoke Test (Sanity)

**What it tests.** That the whole path works at all, with one user and no pressure: the gateway routes requests, the Restaurant service answers the list and the menu, Redis serves the cache, and (with the token) the order write path accepts an order. If smoke fails you have a bug or a missing setting, not a scaling problem. Run it first, every time, before any real load.

**What you will see.** One VU for 30 seconds, about 15 sessions and 30 requests. A healthy run ends with `checks_succeeded 100%` (30 of 30), `browse_errors 0%`, `rate_limited 0`, and green thresholds. On the measured run the menu averaged 16 ms with a worst case of 167 ms. The slow first request is normal: the first calls warm up the cache and the .NET runtime. `orders_placed` is often 0, because only about 2 sessions order.

**What you do.** Make sure all five services are running (section 1) and you logged in (section 2). Then run:

**Bash:**
```bash
k6 run $ORDER_ARGS k6/smoke.js
```

**PowerShell:**
```powershell
k6 run @orderArgs k6/smoke.js
```

If anything is red, **stop here** and fix it before running another test:

| You see | It means | Do this |
|---|---|---|
| `connection refused` or `dial tcp ... refused` | The gateway or a service is not running | Start the five services from section 1 and check ports 8080, 5224, 5240, 5250, 5260 |
| `list 200` or `menu 200` check fails with 502 or 503 | The gateway is up but the Restaurant service is not | Check the Restaurant terminal (port 5260) |
| `rate_limited` above 0, or 429 in the log | The rate limit env vars were not set before the services started | Stop the gateway and the monolith, set `Gateway__RateLimitPerMinute` and `RateLimit__PerMinute` to `100000`, start them again |
| `order 201` check fails with 401 | The token is missing or expired | Log in again (section 2) and rebuild `$orderArgs` / `ORDER_ARGS` |
| `order 201` check fails with 422 | The seed data does not match the order body | Check that the database is seeded and the restaurant ids exist |

### Test 2: Average Load Test (Dinner Rush)

**What it tests.** That Tadka meets its promise on an ordinary busy evening. The test ramps to 50 users in 1 minute, holds them for 3 minutes, then ramps down for 1 minute. The pass rule is the Week 1 SLO: menu and list p99 under 300 ms, and under 1% errors. It is also the first test that really exercises the **write path**: about 15% of sessions place an order, so hundreds of orders flow through the outbox, Kafka, Payment and Delivery while the reads continue.

**What you will see.** About 50 requests per second during the hold (43 averaged over the whole run, ramps included). On the measured run: menu p99 6.6 ms, list p99 10.6 ms, 0 errors, 0 rate-limited, and 875 orders placed. In Grafana (`http://localhost:3000`) the Rate panel is a flat line, Errors stays at 0, and the Duration percentiles stay low. In Jaeger, the `ProcessPayment` traces keep arriving. This is the **baseline**: every later number is compared with it.

**What you do.**

1. Log in again if more than 15 minutes have passed since section 2, and rebuild `$orderArgs` / `ORDER_ARGS`.
2. Open the Grafana RED dashboard before you press enter, so you see the line start to rise.
3. Run the test, and watch Rate, Errors and Duration while the 3 minute hold runs.
4. When it ends, check that all thresholds are `✓` and write down the menu p99 and the requests per second.
5. Check the Payment queue (section 4, Surface 4). It should be close to 0 before the next test.

**Bash:**
```bash
k6 run $ORDER_ARGS k6/average-load.js
```

**PowerShell:**
```powershell
k6 run @orderArgs k6/average-load.js
```

If a threshold shows `✗`, read which one. A `menu p99` above 300 ms with the cache warm usually means Redis is down or the machine is busy with something else. A `browse_errors` above 1% usually means a service crashed: look at the five terminals.

### Test 3: Stress Test (Finding the Knee)

**What it tests.** Where Tadka stops coping. The test climbs in stages: 1 minute at about 50 users, 2 minutes at about 100, 2 minutes at about 200, 2 minutes at 300 (`-e MAX_VUS=...` to change the top), then 1 minute of recovery. Every system has a breaking point, and the point of this test is to find yours before a real IPL final does. The thresholds are deliberately loose (p99 under 5 seconds, errors under 25%): a breach is the **finding**, not a failed test.

**What you will see.** The requests per second rise with each stage: about 150 averaged over the whole run and about 300 at the 300 user stage (each user makes about one request per second). On the measured run the HTTP side never moved: menu p99 5.5 ms, 0 errors, 5,008 orders placed. The saturation is in the queue (see below). In Kafka UI or the lag command from Surface 4, the lag of `tadka-payment` on `order-placed` grows through the second half of the test, and the newest orders stay `Created`.

**What you do.**

1. Log in again just before you start, because the run takes 8 minutes and the token lives 15. Rebuild `$orderArgs` / `ORDER_ARGS`.
2. Write down the Payment lag before you start (it should be near 0).
3. Run the test. At each stage boundary (about 1, 3, 5 and 7 minutes) look at three places: the Grafana Duration panel, the Kafka lag (Surface 4), and the Postgres connections (Surface 3).
4. Note two numbers: the VU count where menu p99 first crossed 300 ms (on a laptop, usually never), and the lag at the end of the run.
5. Wait for the lag to drain before the next test. At about 4 orders per second, 1,000 waiting orders take about 4 minutes.

**Bash:**
```bash
k6 run $ORDER_ARGS k6/stress.js
```

**PowerShell:**
```powershell
k6 run @orderArgs k6/stress.js
```

Observe the knee: at lower concurrency, latency is flat. Once connection pools or CPU saturate, queue wait time grows non-linearly. Note the VU count where p99 crosses the 300 ms SLO and where errors first appear (the run itself only flags the catastrophic end: p99 > 5 s or > 25% errors). That number and the first resource to saturate are the deliverable.

**On a laptop, 300 VUs does not reach the knee.** Each VU thinks for 1 to 3 seconds between requests, so 300 VUs is only about 300 requests per second. See "What a laptop run looks like" below: latency stayed flat the whole way and nothing failed. That is a real result for the HTTP path (large headroom over the design load of about 1.2 orders per second, 25 at burst), but it is not the knee.

**The knee is somewhere else: in the queue.** `POST /orders` returns `201` as soon as the order and its outbox row are saved, so HTTP latency stays flat even when payments fall behind. Payment works through `order-placed` one message at a time (one partition, one consumer, about 200 ms per fake charge), which is about **4 orders per second**. Send orders faster than that and the consumer group `tadka-payment` builds a lag, and new orders stay `Created` until Payment catches up. After the average, stress and spike runs back to back the lag was over 1,000 messages, and at 4 orders per second it took several minutes to drain. Watch it while the test runs (Surface 4 below). That lag, not p99, is the first thing to saturate here, and it is the argument for more partitions and more Payment consumers.

To push the HTTP side too, raise the ceiling, for example `-e MAX_VUS=1500`, and watch k6 itself: once k6 uses a full CPU core, the machine you are measuring has become the bottleneck, not Tadka. We have not measured where the HTTP path breaks on a laptop, so find your own number before class and write it down.

### Test 4: Spike Test (Flash Sale)

**What it tests.** Two things a ramp cannot show: **survival** when traffic jumps in seconds (the IPL final ends, a celebrity tweets, Diwali hits 8 pm), and **recovery** when it falls away just as fast. The profile is: 1 minute calm at 10 users, a jump to 200 users in 10 seconds, 1 minute of flood, a drop back to 10 users in 10 seconds, then a 2 minute recovery window. Recovery matters as much as survival: if p99 stays high after the spike, something is leaking (a connection pool that was not released, a queue that never drains, memory not freed).

**What you will see.** Requests per second jump from about 10 to about 200 at the top of the flood (each user makes about one request per second) and back. On the measured run: menu p99 5.3 ms, 0 errors, no dropped connections, 1,170 orders placed. After the drop, latency returns to the calm level within seconds. The Payment lag jumps during the flood and then slowly drains. **The circuit breaker does not open in this test:** the breaker sits on the Payment call to the payment gateway, and the fake gateway answers fast. See the note below the commands.

**What you do.**

1. Log in again, because the test takes about 4 minutes and a fresh token avoids a surprise 401. Rebuild `$orderArgs` / `ORDER_ARGS`.
2. Run the test and watch the Grafana Rate and Duration panels at three moments: just before the spike, at the top of the flood, and during the last 2 minutes.
3. After the drop, compare latency with the calm minute at the start. They should match. If they do not, write down what is still high, and check Surface 3 (database connections) and Surface 4 (queue lag).
4. Optional, to see the circuit breaker open: during the flood, start the Payment outage from [Day 14, Demo 1](day-14.md), then restore it afterwards.

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

**What it tests.** The cache stampede: what happens when a very popular menu expires and thousands of people ask for it in the same second. In this test 80% of all requests ask for **one** restaurant (Meghana Foods), which is the flash-sale pattern. Redis cache-aside with the single-flight stampede lock (ADR-018, ADR-019) should let **one** request fetch the menu from PostgreSQL while all the others wait for that result, instead of hundreds of requests hitting the database at once. The test uses 80 VUs and only reads menus (no orders, so the token is not needed). The profile is 30 s to 20 users, 1 minute to 80, 2 minutes at 80, 30 s down.

**What you will see.** About 166 requests per second, with a very short think time (0.1 to 0.6 s). The summary adds two counters: `meghana_menu_hits` (about 80% of all requests) and `other_menu_hits` (the rest). On the measured run: `hotkey_errors` 0%, hot-menu p99 5.1 ms, and no 429s. Look for **no spike** in errors or latency in the first seconds after you delete the key: that is the lock stopping the stampede.

**What you do.**

1. **Delete the hot menu key first**, so every request misses at once. Without this step the cache is already warm and the test only shows cache hits:

   **Bash:**
   ```bash
   docker exec tadka-redis redis-cli DEL restaurant:a1b2c3d4-0001-4000-8000-000000000001:menu
   ```

   **PowerShell:**
   ```powershell
   docker exec tadka-redis redis-cli DEL restaurant:a1b2c3d4-0001-4000-8000-000000000001:menu
   ```

   It prints `1` if the key existed (the menu key has a 60 second TTL, so it is often already gone, and `0` is fine).
2. Start the run right away. Watch the first seconds on the Grafana Duration panel, and the hot-menu latency in the k6 progress.
3. Afterwards, check the key is back with a countdown: `docker exec tadka-redis redis-cli TTL restaurant:a1b2c3d4-0001-4000-8000-000000000001:menu` prints a number up to 60.
4. Optional: repeat with `-e HOT_SHARE=0.95 -e PEAK_VUS=100` to make the hot key hotter.

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

### What a laptop run looks like

Measured on a developer laptop with the five services started with `dotnet run` (as in section 1), the Docker stack from section 1, and the rate limits raised. Use these as a rehearsal check: your numbers will differ, but they should look like this and `rate_limited` must be 0.

| Test | Load | Requests per second (run average) | Menu p99 | Errors | Orders placed |
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
