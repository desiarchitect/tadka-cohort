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

**Interview Claim:** "Unsafe write requests carry a client-generated UUID `Idempotency-Key`. The service checks and stores keys inside the local database transaction to prevent duplicate orders or charges on network retries ([ADR-011](../adrs/011-idempotency-for-unsafe-writes.md))."

**Runnable Verification:**

Submit an order with an explicit `Idempotency-Key`:

**Bash:**
```bash
KEY="idem-$(date +%s)"
curl -i -X POST http://localhost:8080/api/v1/orders \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: $KEY" \
  -d '{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"a1b2c3d4-0001-4000-8000-000000000001","items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":2}]}'
```

**PowerShell:**
```powershell
$key = "idem-" + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
curl.exe -i -X POST http://localhost:8080/api/v1/orders `
  -H "Content-Type: application/json" `
  -H "Idempotency-Key: $key" `
  -d '{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"a1b2c3d4-0001-4000-8000-000000000001","items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":2}]}'
```

First request returns `201 Created`. Replaying the identical command returns `200 OK` with the exact cached response body. No duplicate row or charge occurs.

---

### Evidence Demo 2: Outbox Pattern & Consumer Catch-Up

**Interview Claim:** "To avoid two-phase commit across database and broker, services write domain events to a local `OutboxMessages` table in the same transaction as entity state. Background relay workers poll and publish to Kafka ([ADR-028](../adrs/028-transactional-outbox-inbox.md)). When downstream consumers crash, Kafka retains messages without data loss."

**Runnable Verification:**

1. Stop the Payment service or check lag:

**Bash:**
```bash
docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh \
  --bootstrap-server localhost:9092 \
  --command-config /etc/kafka/docker/client.properties \
  --describe --group payment-service-group
```

**PowerShell:**
```powershell
docker exec tadka-kafka /opt/kafka/bin/kafka-consumer-groups.sh `
  --bootstrap-server localhost:9092 `
  --command-config /etc/kafka/docker/client.properties `
  --describe --group payment-service-group
```

When messages arrive while Payment is offline, partition lag increments. Once the consumer starts, lag drains to 0 with zero message loss.

---

### Evidence Demo 3: Polyglot Persistence for Live Geospatial Tracking

**Interview Claim:** "High-frequency GPS pings from thousands of delivery riders (1 ping every 3-5 seconds) would saturate relational B-trees with write-amplification and write locks. We use polyglot persistence: Redis GEO for ephemeral live locations (`GEOADD`/`GEOSEARCH`), and PostgreSQL exclusively for durable delivery assignments and audit history ([ADR-034](../adrs/034-polyglot-persistence-location.md))."

**Runnable Verification:**

Add and query rider coordinates in Redis:

**Bash / PowerShell:**
```bash
docker exec -i tadka-redis redis-cli GEOADD delivery:riders:geo 77.6412 12.9716 "suresh"
docker exec -i tadka-redis redis-cli GEOSEARCH delivery:riders:geo FROMLONLAT 77.6400 12.9700 BYRADIUS 5 km WITHDIST
```

Query execution completes in sub-millisecond in-memory time using geohash integer representations.

---

### Evidence Demo 4: Local Read Model & Event-Carried State Transfer

**Interview Claim:** "Extracting the Restaurant service removed menu management from the monolith, but order pricing sits on the critical path. Making synchronous HTTP calls to Restaurant during checkout re-creates temporal coupling. We use Event-Carried State Transfer: Ordering maintains a local read model (`ordering.menu_replica`) populated by Kafka `menu-updated` events ([ADR-037](../adrs/037-event-carried-state-transfer-read-model.md))."

**Runnable Verification:**

Kill the Restaurant API container or process:

**Bash / PowerShell:**
```bash
docker stop tadka-restaurant-db
```

Submit an order to the Ordering API (`POST http://localhost:8080/api/v1/orders`). The order succeeds immediately because Ordering prices items from its local replica. Restore the database afterwards:

**Bash / PowerShell:**
```bash
docker start tadka-restaurant-db
```

---

### Evidence Demo 5: Single-Trace Distributed Observability

**Interview Claim:** "Asynchronous messaging across Outbox tables and Kafka brokers breaks standard in-memory trace propagation. We persist the W3C `traceparent` string into the database outbox row, inject it into Kafka record headers, and extract it in consumers ([ADR-041](../adrs/041-trace-context-propagation-http-kafka-outbox.md))."

**Runnable Verification:**

Open Jaeger in your browser: `http://localhost:16686`. Search for traces under service `tadka-gateway` or `tadka-ordering`. Inspect a complete 9-span checkout trace showing Gateway, Ordering controller, Outbox publish, Kafka queueing, and Payment consumption on one unified waterfall timeline.

---

### Evidence Demo 6: External Circuit Breaker & Graceful Degradation

**Interview Claim:** "Third-party bank gateways experience brownouts and rate limits. A Polly circuit breaker wraps external calls with a 2-second timeout and exponential backoff ([ADR-043](../adrs/043-circuit-breaker-retry-backoff.md)). On outage, Buffer mode protects orders by seeking consumer offsets back, while Compensate mode initiates automated refunds."

**Runnable Verification:**

Verify circuit breaker transition metrics in Prometheus (`http://localhost:9090`):

```promql
tadka_payment_circuit_transitions_total
```

---

## 3. Running the Breadth Toy Demos

The `toydemo/` directory contains 13 focused runnable demonstrations illustrating system design patterns outside the core OLTP path.

Run the entire inventory smoke test:

**Bash (Git Bash):**
```bash
powershell -ExecutionPolicy Bypass -File ./toydemo/verify-all.ps1 -Run fix
```

**PowerShell:**
```powershell
powershell -ExecutionPolicy Bypass -File .\toydemo\verify-all.ps1 -Run fix
```

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

Ensure all 207 tests pass across the five test projects:

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

The response returns `{ "accessToken": "ey...", "expiresInSeconds": 3600, "role": "Customer" }`. You will supply this `accessToken` to k6 on Day 16.
