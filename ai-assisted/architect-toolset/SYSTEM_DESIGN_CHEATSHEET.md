# System Design Interview & Production Field Cheatsheet

> **Desi Architect Cohort, Quick-Reference Cheatsheet**  
> Use this reference for System Design Interviews (HLD/LLD), capacity planning, and production architectural reviews.

---

## 1. Latency Hierarchy Every Architect Must Know

| Group | Operation | Real-World Latency | Scaled Analogy (1 ns = 2 seconds) |
| :--- | :--- | :--- | :--- |
| CPU | L1 cache reference | 0.5 ns | 1 second |
| CPU | Branch mispredict | 5 ns | 10 seconds |
| CPU | L2 cache reference | 7 ns | 14 seconds |
| CPU | Mutex lock and unlock | 25 ns | 50 seconds |
| Memory | Main memory (RAM) reference | 100 ns | 3.3 minutes |
| CPU | Compress 1 KB with Snappy | 2,000 ns (2 µs) | 1 hour |
| Memory | Read 1 MB sequentially from memory | 3,000 ns (3 µs) | 1.5 hours |
| SSD | NVMe random read | 10,000 ns (10 µs) | 5 hours |
| SSD | Read 1 MB sequentially from NVMe | 250,000 ns (250 µs) | 5.8 days |
| HDD | Hard drive seek | 10 ms | 8 months |
| Network | Send 1 MB over a 1 Gbps local network | 10 ms | 8 months |
| WAN | Round trip Mumbai to Singapore (approximate) | about 60 ms | about 4 years |
| WAN | Round trip Mumbai to London (approximate) | about 120 ms | about 8 years |
| WAN | Round trip Mumbai to California (approximate) | about 220 ms | about 14 years |

Round-trip times vary by route and provider. Use them for the order of magnitude, not as a promise.

---

## 2. Capacity Planning Math & Estimations

### Time Conversions (Round for Fast Math)
- 1 Day = $24 \text{ hours} \times 3,600 \text{ sec} = 86,400 \text{ seconds} \approx \mathbf{100,000 \text{ seconds}}$ (for mental math)
- 1 Million requests/day $\approx \frac{1,000,000}{100,000} = \mathbf{10 \text{ requests/sec}}$
- 10 Million requests/day $\approx \mathbf{100 \text{ requests/sec}}$
- 100 Million requests/day $\approx \mathbf{1,000 \text{ requests/sec}}$
- 1 Billion requests/day $\approx \mathbf{10,000 \text{ requests/sec}}$

### Data Size Math
- 1 Byte = 8 bits
- 1 KB = $10^3$ bytes ($2^{10} = 1,024$)
- 1 MB = $10^6$ bytes ($2^{20} = 1,048,576$)
- 1 GB = $10^9$ bytes ($2^{30} \approx 10^9$)
- 1 TB = $10^{12}$ bytes ($2^{40} \approx 10^{12}$)
- 1 PB = $10^{15}$ bytes ($2^{50} \approx 10^{15}$)

### Estimation Formulas
- **Throughput / QPS**:
  $$\text{Average QPS} = \frac{\text{DAU} \times \text{Average Requests per User per Day}}{86,400}$$
  $$\text{Peak QPS} = \text{Average QPS} \times 2 \text{ to } 5$$
- **Storage Growth (Annual)**:
  $$\text{Storage/Year} = \text{Write QPS} \times 86,400 \times \text{Average Payload Size (Bytes)} \times 365$$
  *Always add $20\%\text{ to }30\%$ overhead for database indexes, metadata, and replication factor ($3\times$).*
- **RAM Cache Sizing (80/20 Rule)**:
  - Cache $20\%$ of daily read data volume.
  $$\text{RAM Needed} = \text{Daily Read Data Volume} \times 0.20$$
- **Network Bandwidth**:
  $$\text{Ingress/Egress Bandwidth} = \text{QPS} \times \text{Average Request/Response Payload Size}$$

---

## 3. The 45-Minute System Design Interview Blueprint

```
┌────────────────────────────────────────────────────────────────────────┐
│ Phase 1: Requirements Clarification (00 - 05 min)                      │
│ - Functional requirements (Core 3-4 user journeys)                     │
│ - Non-functional requirements (Scale, Latency p99, Availability, SLO)  │
│ - Out of Scope boundaries (Explicitly define what you won't solve)     │
├────────────────────────────────────────────────────────────────────────┤
│ Phase 2: Back-of-the-Envelope Estimations (05 - 10 min)                │
│ - Read QPS, Write QPS, Peak multipliers                                │
│ - Storage per year, Bandwidth ingress/egress, RAM cache size           │
├────────────────────────────────────────────────────────────────────────┤
│ Phase 3: High-Level Architecture (10 - 20 min)                         │
│ - Draw end-to-end data flow: Client -> CDN/DNS -> Gateway -> Services  │
│ - Primary DB vs Replica vs Cache vs Message Queue                      │
│ - State core API contracts (e.g. POST /api/v1/orders)                  │
├────────────────────────────────────────────────────────────────────────┤
│ Phase 4: Deep Dive into Core Bottlenecks (20 - 35 min)                 │
│ - The Hard Problem (e.g. Transactional Outbox, Cache stampede, OCC)    │
│ - Data schema design (Relational vs NoSQL, indexes, partitioning key)  │
│ - Concurrency, race conditions & idempotency keys                      │
├────────────────────────────────────────────────────────────────────────┤
│ Phase 5: Resilience, Observability & Bottlenecks (35 - 45 min)         │
│ - What happens when X fails? (Circuit breakers, bulkheads, timeouts)   │
│ - Distributed tracing (W3C traceparent), RED metrics                   │
│ - Scaling knee: Where does this architecture break next?               │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 4. Architecture Decision Trees

### 4.1 Database Selection Decision Tree
- Need strict ACID, multi-row transactions, complex joins, and financial correctness?  
  $\rightarrow$ **Relational (PostgreSQL / MySQL)**
- Need flexible schema, hierarchical documents, and high write volume per document?  
  $\rightarrow$ **Document Store (MongoDB / DynamoDB)**
- Need sub-millisecond caching, rate-limiting, live geo tracking, or pub/sub?  
  $\rightarrow$ **In-Memory Key-Value (Redis)**
- Need append-only, high-throughput time-series metrics or IoT data?  
  $\rightarrow$ **Time-Series (TimescaleDB / Prometheus)**
- Need full-text search, typo-tolerance, and faceted filtering?  
  $\rightarrow$ **Search Index (Elasticsearch / OpenSearch)**
- Need highly interconnected graph relationships (fraud rings, social graphs)?  
  $\rightarrow$ **Graph Database (Neo4j / Amazon Neptune)**

### 4.2 Messaging & Queueing Decision Tree
- Need strict partition-level ordering, durable replayable event log, and high throughput streaming?  
  $\rightarrow$ **Distributed Event Log (Kafka / Redpanda / AWS Kinesis)**
- Need complex routing (fan-out, topic exchanges), AMQP standard, and fine-grained per-message ack?  
  $\rightarrow$ **Message Broker (RabbitMQ)**
- Need simple managed worker queue with minimal maintenance?  
  $\rightarrow$ **Cloud Queue (AWS SQS / Azure Service Bus)**

### 4.3 CAP & PACELC Theorem Quick Reference
- **CAP Theorem**: In a network partition (**P**), you must choose between Consistency (**C**) or Availability (**A**).
- **PACELC Theorem**:  
  **If Partition (P)**: Choose Availability (**A**) or Consistency (**C**).  
  **Else (E)**: Choose Latency (**L**) or Consistency (**C**).
  - *Example*: Tadka's primary Postgres on its own is consistent (**PC / EC**). Reads from its **asynchronous** replica trade consistency for latency (replica lag, ADR-016 and ADR-063): a user can place an order and not see it for a moment, which is why a read right after a write stays on the primary.
  - *Example*: DynamoDB / Cassandra with eventual consistency = **PA / EL** (Available and Low Latency over Consistency).

---

## 5. Resiliency & Chaos Checklist

- [ ] **Hard Timeouts**: Are all external outbound HTTP, database, and broker calls configured with hard deadlines (e.g. 2.0s)?
- [ ] **Jittered Retries**: Are retries configured with exponential backoff and randomized jitter to prevent thundering herd waves?
- [ ] **Transport-Only Predicates**: Are retries and circuit breakers restricted to network/transport failures, strictly excluding business 400/402/404 errors?
- [ ] **Circuit Breakers**: Does the breaker define a minimum throughput ($\ge 5$ requests) before evaluating error ratios?
- [ ] **Bulkheads**: Are thread pools or connection limits isolated so a hanging third party cannot exhaust process threads?
- [ ] **Cache Fallbacks**: If Redis dies, does the application degrade gracefully to the primary DB with single-flight mutexes?
- [ ] **Idempotency**: Do all write endpoints accept an `Idempotency-Key` header and enforce unique deduplication in an Inbox table?
- [ ] **Trace Context**: Does every message published to Kafka carry the W3C `traceparent` header to preserve end-to-end tracing in Jaeger?
- [ ] **Queue lag**: Do you watch consumer lag and the number of orders waiting, not only HTTP p99? A fast API can hide a slow pipeline.
- [ ] **Poison messages**: Does a message that fails repeatedly go to a dead-letter topic instead of blocking the partition (ADR-051)?

---

## 6. Tadka's Own Numbers

| Thing | Number | Where it comes from |
| :--- | :--- | :--- |
| Design load | 1 lakh orders/day = about 1.2 orders/s average, about 5.5/s sustained peak, about 25/s burst | The course canon |
| SLO | Menu and list p99 under 300 ms, under 1% errors | `k6/lib.js` thresholds |
| Gateway rate limit (default) | 120 requests per minute per IP | ADR-035 (raise it for load tests) |
| Payment resilience | Bulkhead 10, 2 retries, breaker at ratio 0.5 with at least 5 calls over 30 s and open for 60 s, 2 s timeout | `PaymentOptions.cs` |
| Payment throughput | About 4.4 orders/s from one partition and one consumer | Measured on a laptop, Day 16 |
| HTTP at 300 virtual users | About 300 requests/s, menu p99 about 5 ms, 0 errors | Measured on a laptop, Day 16 |
| Backlog after a 1-minute burst at 25 orders/s | About 1,200 orders, about 6 minutes to clear | Arithmetic: (25 - 4.4) x 60, then drained at the spare capacity |
| Cost per month at 1 lakh orders/day | About Rs 6,100 (honest monolith) versus about Rs 31,300 (what we built) | `docs/cost-model.md`, a model not a bill |
| Cost at 10 lakh orders/day | About Rs 88,000 | `docs/cost-model.md` |
| Tests | 207 pass (Orders 115, Payment 35, Delivery 23, Restaurant 19, Gateway 15) | `dotnet test` |
