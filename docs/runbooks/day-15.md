# Day 15: Runbook: System Design Masterclass, Interview Transfer & Breadth Toys

**Branch:** `day-15` · **What changed since Day 14:** [`docs/changelog.md`](../changelog.md).

Day 14 made the 4-service system survivable under chaos. Day 15 is the **interview-readiness and breadth milestone**. On Day 15, we do not write new application code. Instead, we map the entire 14-day production codebase into concrete, defensible evidence for a 45-minute senior system design interview, verify the breadth toy demos, and prepare our environment for Day 16 load testing.

> **The Core Architectural Mindset:** An architect does not memorize answers; an architect defends trade-offs with empirical evidence. Every box, arrow, and protocol choice in your system design interview should be backed by an ADR and a working implementation you have run and broken yourself.

Every command below is provided in both Bash and PowerShell.

---

## 1. The 45-Minute System Design Framework

In an interview, execute the 6-phase framework with strict time discipline:

| Phase | Time | Goal | Tadka Evidence & ADR Anchor |
|---|---|---|---|
| **1. Clarify** | 5 min | Scope functional requirements, identify non-goals, define user personas | Core checkout, payment, menu browsing, order tracking |
| **2. Estimate** | 5 min | Back-of-the-envelope scale: QPS, read/write ratios, storage, bandwidth | 1 lakh orders/day (~1.2 avg, ~25 burst write QPS); rider GPS tracking |
| **3. High-Level Design (HLD)** | 10 min | Establish boundaries, synchronous vs asynchronous paths, edge gateway | 4 services + YARP gateway ([ADR-035](../adrs/035-api-gateway-yarp.md)), Kafka event backbone ([ADR-027](../adrs/027-kafka-async-event-backbone.md)) |
| **4. Deep Dives** | 15 min | Detail 2 or 3 critical architectural mechanisms | Server pricing ([ADR-037](../adrs/037-event-carried-state-transfer-read-model.md)), Outbox/Inbox ([ADR-028](../adrs/028-transactional-outbox-inbox.md)), Geo persistence ([ADR-034](../adrs/034-polyglot-persistence-location.md)) |
| **5. Bottlenecks & Failure** | 7 min | Identify single points of failure, partition modes, circuit breakers | Circuit breaker ([ADR-043](../adrs/043-circuit-breaker-retry-backoff.md)), Graceful degradation ([ADR-044](../adrs/044-graceful-degradation-dependency-classification.md)) |
| **6. Wrap & Review** | 3 min | Summarize trade-offs, state what to monitor, outline revisit triggers | RED metrics ([ADR-042](../adrs/042-metrics-cardinality-limits.md)), SLO targets ([ADR-044](../adrs/044-graceful-degradation-dependency-classification.md)) |

---

## 2. Using Tadka as Interview Evidence

When an interviewer asks how your design handles real-world failure, cite the exact patterns implemented in Tadka. You can demonstrate each one on this branch.

### Prerequisites: Ensure Infra and Services Are Running

If containers are not already running:

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

Build the solution once:

**Bash / PowerShell:**
```bash
dotnet build Tadka.slnx
```

---

### Evidence Demo 1: Idempotency (Never Double Charge)

**Interview Claim:** "Unsafe write requests carry a client-generated UUID `Idempotency-Key`. The key is the primary key of `ordering.idempotency_keys` and is saved in the same transaction as the order, so a retry or a double-tap can never create a second order ([ADR-011](../adrs/011-idempotency-for-unsafe-writes.md))."

**Runnable Verification:**

Orders require a JWT (ADR-030), so log in as the seeded customer Priya first, then submit the order from the ready-made body `docs/runbooks/place-order.json` with an explicit `Idempotency-Key`:

**Bash:**
```bash
TOKEN=$(curl -s -X POST http://localhost:8080/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"priya@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
KEY="idem-$(date +%s)"
curl -i -X POST http://localhost:8080/api/v1/orders \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Idempotency-Key: $KEY" \
  --data @docs/runbooks/place-order.json
```

**PowerShell:**
```powershell
$token = (curl.exe -s -X POST http://localhost:8080/api/v1/auth/login `
  -H "Content-Type: application/json" `
  -d '{\"email\":\"priya@tadka.test\",\"password\":\"Password123!\"}' | ConvertFrom-Json).accessToken
$key = "idem-" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
curl.exe -i -X POST http://localhost:8080/api/v1/orders `
  -H "Content-Type: application/json" `
  -H "Authorization: Bearer $token" `
  -H "Idempotency-Key: $key" `
  --data "@docs/runbooks/place-order.json"
```

Re-run only the last `curl` (same `$KEY` / `$key`) to replay. **Captured live:** the first request returns `201 Created`. The replay returns `200 OK` with the order that key created (it is read back from the database, so its status may already have moved on, for example to `Confirmed`). No second order is created. Prove it with the table that holds the key:

**Bash:**
```bash
docker exec tadka-postgres psql -U tadka -d tadka -t -A \
  -c "select \"Key\", \"OrderId\" from ordering.idempotency_keys where \"Key\"='$KEY';"
```

**PowerShell:**
```powershell
docker exec tadka-postgres psql -U tadka -d tadka -t -A `
  -c "select \`"Key\`", \`"OrderId\`" from ordering.idempotency_keys where \`"Key\`"='$key';"
```

**Why a database constraint and not just an `if`.** [`OrdersController.Create`](../../src/Tadka.Api/Controllers/OrdersController.cs) first looks the key up, which handles a retry that arrives later. But two requests that arrive at the same moment can both miss that lookup. The key is the table's primary key ([`IdempotencyKeyConfiguration.cs`](../../src/Tadka.Api/Data/Configurations/IdempotencyKeyConfiguration.cs)), so the second insert fails, and the controller catches that unique violation and returns the winner's order as `200`. Try six simultaneous requests with one key:

**Bash:**
```bash
KEY2="race-$(date +%s)"
for i in 1 2 3 4 5 6; do
  (curl -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:8080/api/v1/orders \
    -H "Content-Type: application/json" -H "Authorization: Bearer $TOKEN" \
    -H "Idempotency-Key: $KEY2" --data @docs/runbooks/place-order.json) &
done; wait
```

**PowerShell:**
```powershell
$key2 = "race-" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$body = (Resolve-Path docs\runbooks\place-order.json).Path
$jobs = 1..6 | ForEach-Object { Start-Job -ArgumentList $token, $key2, $body {
  param($t, $k, $b)
  curl.exe -s -o NUL -w "%{http_code}" -X POST http://localhost:8080/api/v1/orders `
    -H "Content-Type: application/json" -H "Authorization: Bearer $t" `
    -H "Idempotency-Key: $k" --data "@$b" } }
$jobs | Wait-Job | Receive-Job | Group-Object | ForEach-Object { $_.Name + " x " + $_.Count }
```

**Captured live:** one `201` and five `200`, all with the same order id, and exactly one row in `idempotency_keys`.

---

### Evidence Demo 2: Outbox Pattern & Consumer Catch-Up

**Interview Claim:** "To avoid two-phase commit across database and broker, services write domain events to a local `OutboxMessages` table in the same transaction as entity state. Background relay workers poll and publish to Kafka ([ADR-028](../adrs/028-transactional-outbox-inbox.md)). When downstream consumers crash, Kafka retains messages without data loss."

**Runnable Verification:**

1. Stop the Payment service (Ctrl+C its `dotnet run` terminal), place an order as in Demo 1, then check consumer lag (it shows lag 1). Restart Payment and re-run the command; lag drains to 0:

**Bash:**
```bash
docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh \
  --bootstrap-server localhost:9092 \
  --command-config /etc/kafka/docker/client.properties \
  --describe --group tadka-payment
```

**PowerShell:**
```powershell
docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh `
  --bootstrap-server localhost:9092 `
  --command-config /etc/kafka/docker/client.properties `
  --describe --group tadka-payment
```

When messages arrive while Payment is offline, partition lag increments. Once the consumer starts, lag drains to 0 with zero message loss.

---

### Evidence Demo 3: Polyglot Persistence for Live Geospatial Tracking

**Interview Claim:** "High-frequency GPS pings from thousands of delivery riders (a ping every few seconds) would saturate a relational table with write amplification and write locks. We use polyglot persistence: Redis GEO for the ephemeral latest location, and PostgreSQL for durable delivery assignments and audit history ([ADR-034](../adrs/034-polyglot-persistence-location.md))."

**What Tadka itself does.** [`LocationStore.cs`](../../src/Tadka.Delivery.Api/LocationStore.cs) calls `GEOADD delivery:agents` to overwrite a rider's latest position (one member per rider id, no growing table) and `GEOPOS` to read it back for the customer's tracking call. Assignment today picks the first available rider and claims it atomically ([`DeliveryService.cs`](../../src/Tadka.Delivery.Api/DeliveryService.cs)). The `GEOSEARCH` below is the extra step an interview design adds in front of that claim, to shortlist riders near the restaurant. The same Redis structure supports both.

**Runnable Verification:**

Add two demo riders (one near, one far) and ask for riders within 5 km, nearest first, then remove them again so the real riders' key is left clean:

**Bash / PowerShell:**
```bash
docker exec -i tadka-redis redis-cli GEOADD delivery:agents 77.6412 12.9716 "demo-rider-near"
docker exec -i tadka-redis redis-cli GEOADD delivery:agents 77.6900 13.0300 "demo-rider-far"
docker exec -i tadka-redis redis-cli GEOSEARCH delivery:agents FROMLONLAT 77.6400 12.9700 BYRADIUS 5 km WITHDIST ASC
docker exec -i tadka-redis redis-cli ZREM delivery:agents demo-rider-near demo-rider-far
```

**Captured live:** only `demo-rider-near` comes back, at about `0.22` km. The far rider is about 8 km away and is filtered out. Redis stores each point as a 52-bit geohash inside a sorted set (that is why `ZREM` removes a GEO member), which is what makes the radius query a cheap range lookup instead of a scan. The real API path for a rider posting a location and a customer tracking it is in the [Day 11 runbook](day-11.md), section 3.

---

### Evidence Demo 4: Local Read Model & Event-Carried State Transfer

**Interview Claim:** "Extracting the Restaurant service removed menu management from the monolith, but order pricing sits on the critical path. Making a synchronous HTTP call to Restaurant during checkout re-creates temporal coupling. We use Event-Carried State Transfer: Ordering prices from its own read model (`ordering.menu_replica`), populated by Kafka `menu-updated` events ([ADR-037](../adrs/037-event-carried-state-transfer-read-model.md))."

**Runnable Verification:** show the wound first, then the fix.

Stopping the Restaurant **database** is not a fair test, because the Restaurant service answers menu reads from its Redis cache. Stop the Restaurant **service** (Ctrl+C in its terminal). Then restart the monolith in the coupled mode, which prices every order with a synchronous call to Restaurant:

**Bash:**
```bash
export Ordering__RestaurantReadMode=SyncHttp
export OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"
dotnet run --project src/Tadka.Api
```

**PowerShell:**
```powershell
$env:Ordering__RestaurantReadMode = "SyncHttp"
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
dotnet run --project src/Tadka.Api
```

Log in again (the monolith's signing keys reset on restart) and send the Demo 1 request with a new `Idempotency-Key`. **Captured live:** `500` after about 2 seconds. The Restaurant service is down, so pricing, and with it every checkout, is down.

Now clear the variable and restart the monolith in its default mode (`LocalReplica`), with Restaurant still stopped:

**Bash:**
```bash
unset Ordering__RestaurantReadMode
dotnet run --project src/Tadka.Api
```

**PowerShell:**
```powershell
$env:Ordering__RestaurantReadMode = $null
dotnet run --project src/Tadka.Api
```

Send the order again (new key). **Captured live:** `201` in milliseconds, with a total of ₹598 (2 x Chicken Biryani), priced from `ordering.menu_replica` ([`LocalReplicaPricingSource.cs`](../../src/Tadka.Api/Infrastructure/RestaurantReadModel/LocalReplicaPricingSource.cs)), because Ordering never calls Restaurant on the order path. Start the Restaurant service again afterwards.

The price you read is only as fresh as the last `menu-updated` event Ordering applied. The gauge `tadka.replica.lag_seconds` ([ADR-063](../adrs/063-replica-lag-metric.md)) shows how stale it can be.

---

### Evidence Demo 5: Single-Trace Distributed Observability

**Interview Claim:** "Asynchronous messaging across Outbox tables and Kafka brokers breaks standard in-memory trace propagation. We persist the W3C `traceparent` string into the database outbox row, inject it into Kafka record headers, and extract it in consumers ([ADR-041](../adrs/041-trace-context-propagation-http-kafka-outbox.md))."

**Runnable Verification:**

Place an order (Demo 1 request), wait about 5 seconds, then open Jaeger in your browser: `http://localhost:16686`. Choose service `Tadka.Gateway`, press **Find Traces**, and open the newest trace. **Captured live:** one trace with **13 spans across 5 services** (Gateway, Ordering, Payment, Delivery, Restaurant), about 1.5 seconds end to end, showing the Gateway call, the Ordering controller, each Outbox publish, each Kafka consume and the `ProcessPayment` business span on one waterfall. The span-by-span table is in the [Day 13 runbook](day-13.md), section 3.

---

### Evidence Demo 6: External Circuit Breaker & Graceful Degradation

**Interview Claim:** "Third-party bank gateways experience brownouts and rate limits. A Polly pipeline wraps the external call: a bulkhead, a jittered retry for transport failures only, a circuit breaker, and a 2-second per-attempt timeout ([ADR-043](../adrs/043-circuit-breaker-retry-backoff.md)). On an outage, Buffer mode protects orders by seeking the consumer offset back and pausing. Compensate mode (the default) saves the payment as `Failed` and the saga cancels the order."

**Runnable Verification:**

Restart the Payment service with a gateway that is down and a 15 second break (default is 60), wait about 45 seconds for the consumer handover, and place six orders:

**Bash:**
```bash
export OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"
export Payment__CircuitBreakSeconds=15
export Payment__Gateway__Behavior=Outage
dotnet run --project src/Tadka.Payment.Api
# in another terminal, with $TOKEN from Demo 1:
for i in 1 2 3 4 5 6; do
  curl -s -o /dev/null -w "%{http_code} " -X POST http://localhost:8080/api/v1/orders \
    -H "Content-Type: application/json" -H "Authorization: Bearer $TOKEN" \
    -H "Idempotency-Key: out-$i-$RANDOM" --data @docs/runbooks/place-order.json
done
```

**PowerShell:**
```powershell
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
$env:Payment__CircuitBreakSeconds = "15"
$env:Payment__Gateway__Behavior = "Outage"
dotnet run --project src/Tadka.Payment.Api
# in another terminal, with $token from Demo 1:
1..6 | ForEach-Object {
  curl.exe -s -o NUL -w "%{http_code} " -X POST http://localhost:8080/api/v1/orders `
    -H "Content-Type: application/json" -H "Authorization: Bearer $token" `
    -H "Idempotency-Key: out-$_-$(Get-Random)" --data "@docs/runbooks/place-order.json"
}
```

Every order is accepted with `201` (Ordering never waits for Payment), the Payment terminal prints `Payment circuit OPENED`, and the orders end `Cancelled` (Compensate mode). After about a minute (the metric export interval) verify the breaker in Prometheus (`http://localhost:9090`):

```promql
sum by (state) (tadka_payment_circuit_transitions_total)
```

**Captured live:** `open` 1. Restore the Payment service by restarting it without `Payment__Gateway__Behavior`. The full breaker lifecycle (half-open probe, recovery, Buffer mode) is in the [Day 14 runbook](day-14.md).

---

## 3. Running the Breadth Toy Demos

The `toydemo/` directory contains 13 focused runnable demonstrations illustrating system design patterns outside the core OLTP path.

Run the entire inventory smoke test (needs PowerShell 7 `pwsh`/`powershell`, Node 18+, and for the DB toys `docker compose up -d postgres redis kafka`):

**Bash (no PowerShell needed):**
```bash
for t in search-index:real-db.js olap-cdc:real-db.js object-storage:real-demo.js web-crawler:real-crawl.js crdt-counter:index.js video-hls-cdn:real-demo.js; do
  d=toydemo/day-15-breadth/${t%%:*}-toy
  (cd "$d" && { [ "${t%%:*}" = video-hls-cdn ] && node setup-media.js; node "${t##*:}" --mode=fix; }) || echo "FAILED: $t"
done
```

**Bash (Git Bash, with PowerShell installed):**
```bash
powershell -ExecutionPolicy Bypass -File ./toydemo/verify-all.ps1 -Run fix
```

**PowerShell:**
```powershell
powershell -ExecutionPolicy Bypass -File .\toydemo\verify-all.ps1 -Run fix
```

`verify-all.ps1` runs all **13** toys (Day 3: cursor pagination, gRPC versus REST; Day 6: rate limiter, hot-key stampede, stateful WebSocket; Day 9: notification fan-out, stream processing; Day 15: the six below). **Captured live:** 13 of 13 pass in `fix` mode, in about 10 seconds.

### Which toy backs which interview topic

| Interview topic | Toy | What it shows (captured live) |
|---|---|---|
| Restaurant search (Zomato card) | Search Index | `LIKE '%biryani%'` scans 100,000 rows in about 70 ms. A GIN full-text index answers the same query in about 3.5 ms |
| Deep pagination | `day-03-api-primitives/cursor-pagination-toy` | `OFFSET 80000` walks 80,020 rows (about 12 ms, 729 buffers). A cursor (`id > 80000`) reads 6 buffers (about 0.07 ms) |
| Notification system (mock card C) | `day-09-kafka-async/notification-fanout-toy` | Sync fan-out to 10,000 users: 2,500 delivered, 7,500 missed, a timeout and duplicate retries. One Kafka event plus workers, inbox and DLQ: 9,800 delivered, 200 to the DLQ, 0 missed |
| Cache stampede | `day-06-cache-realtime/hot-key-stampede-toy` | 200 concurrent misses on one hot key issue 200 database queries. With a single-flight lock they issue 1 |
| Object storage, CDN, crawler, CRDT, OLAP | The other Day 15 toys | One pattern each, small enough to explain in two minutes |

### Day 15 Breadth Toys Inventory

| Toy | Path | Core System Design Pattern | Primary Command |
|---|---|---|---|
| **Search Index** | `toydemo/day-15-breadth/search-index-toy` | Full-text search and GIN inverted index vs seq scans | `node real-db.js --mode=fix` |
| **Video HLS CDN** | `toydemo/day-15-breadth/video-hls-cdn-toy` | HLS video chunking, m3u8 playlists, edge cache hits | `node setup-media.js; node real-demo.js --mode=fix` |
| **OLAP / CDC** | `toydemo/day-15-breadth/olap-cdc-toy` | Change Data Capture rollups vs expensive OLTP aggregates | `node real-db.js --mode=fix` |
| **Object Storage** | `toydemo/day-15-breadth/object-storage-toy` | Direct client upload/download using pre-signed S3 URLs | `node real-demo.js --mode=fix` |
| **Web Crawler** | `toydemo/day-15-breadth/web-crawler-toy` | Politeness limits (429 avoidance), visited dedup, trap depth | `node real-crawl.js --mode=fix` |
| **CRDT Counter** | `toydemo/day-15-breadth/crdt-counter-toy` | Conflict-Free Replicated Data Types, state-based G-Counter | `node index.js --mode=fix` |

Run any toy individually by navigating to its subfolder and executing its runner:

**PowerShell:**
```powershell
cd toydemo\day-15-breadth\search-index-toy
node real-db.js --mode=fix
cd ..\..\..
```

**Bash:**
```bash
cd toydemo/day-15-breadth/search-index-toy
node real-db.js --mode=fix
cd ../../..
```

---

## 4. The Peer Mock Interview Kit

In Day 15, students pair up for two 15-minute mock interview rounds.

### Timing Protocol

1. Round 1 (15 min): Student A is Candidate, Student B is Interviewer (10 min interview + 5 min peer feedback).
2. Switch (1 min): Swap roles.
3. Round 2 (15 min): Student B is Candidate, Student A is Interviewer (10 min interview + 5 min peer feedback).

### 100-Point Peer Feedback Rubric

Scores are for peer guidance and feedback only (no instructor grades):

| Category | Points | What Good Looks Like |
|---|---|---|
| **Requirements & Scope** | 20 pts | Clarifies functional goals, states non-goals explicitly, identifies read vs write traffic |
| **Estimation & Scale** | 10 pts | States assumptions upfront, calculates QPS and storage realistically |
| **High-Level Design** | 20 pts | Clean boundaries, labels all communication arrows, separates sync vs async |
| **Deep-Dive Mechanisms** | 20 pts | Explains specific mechanisms (Outbox, idempotency key, Redis GEO, read models) |
| **Resilience & Bottlenecks** | 20 pts | Identifies failure modes, handles network partitions, applies circuit breakers |
| **Communication & Navigation** | 10 pts | Drives the conversation, checks in with interviewer, structures thoughts clearly |

Score bands:

| Score Range | Readiness Level |
|---|---|
| **80 to 100** | Ready for senior architect and staff system design loops. |
| **65 to 79** | Solid fundamentals; needs deeper explanation of failure modes. |
| **Below 65** | Needs more practice driving the 45-minute structure and stating trade-offs. |

---

## 5. Preparing for Day 16 (Load Testing & Capstone)

Day 16 subjects Tadka to intense k6 load testing, calculates infrastructure cost per 1,000 orders, and reviews the portfolio architecture document. Complete these prerequisites before Day 16:

### 1. Install k6

**Windows (PowerShell):**
```powershell
winget install GrafanaLabs.k6
```

**macOS (Homebrew):**
```bash
brew install k6
```

**Verify Installation:**
```bash
k6 version
```

### 2. Verify Solution Tests

Ensure all tests pass across the five test projects (Testcontainers needs Docker running; build first, see section 2). Expect 207 passed:

**Bash / PowerShell:**
```bash
dotnet test Tadka.slnx --no-build
```

### 3. Obtain Seeded Authentication Tokens

Day 16 load tests require an authenticated customer token for write scenarios. Generate a token for seeded customer Priya:

**Bash:**
```bash
curl -s -X POST http://localhost:8080/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"priya@tadka.test","password":"Password123!"}'
```

**PowerShell:**
```powershell
curl.exe -s -X POST http://localhost:8080/api/v1/auth/login `
  -H "Content-Type: application/json" `
  -d '{"email":"priya@tadka.test","password":"Password123!"}'
```

The response returns `{ "accessToken": "ey...", "expiresInSeconds": 900, "role": "Customer", "refreshToken": "..." }`. You will supply this `accessToken` to k6 on Day 16.
