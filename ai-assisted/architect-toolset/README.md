# The Software Architect's Field Manual

> **Desi Architect Cohort, Grand Finale Capstone Edition**  
> *"Architecture is the decisions that are hard to change later."*, Ralph Johnson  
> *"Code is the evidence the design works, not the starting point."*, The Architect's Sequence

---

> **How to trust this document.** The facts about Tadka (ports, ADR numbers, the resilience pipeline, order states, measured numbers) were checked against the code on branch `day-16`. The ADRs run 001 to 067 and there is no 046. For the full list, see [ADR_INDEX.md](ADR_INDEX.md).

## Table of Contents
1. [The Architect's Mindset & Operating Model](#1-the-architects-mindset--operating-model)
2. [Architectural Styles & Evolution Mechanics](#2-architectural-styles--evolution-mechanics)
3. [Distributed Systems Patterns & Messaging](#3-distributed-systems-patterns--messaging)
4. [Data Architecture, Storage & Persistence](#4-data-architecture-storage--persistence)
5. [Resilience, Fault Tolerance & Chaos Engineering](#5-resilience-fault-tolerance--chaos-engineering)
6. [Observability & Distributed Telemetry](#6-observability--distributed-telemetry)
7. [Security Architecture & Zero Trust](#7-security-architecture--zero-trust)
8. [Visual Modeling & Architecture Diagrams (C4 Model)](#8-visual-modeling--architecture-diagrams-c4-model)
9. [System Design Interview Playbook & Capacity Math](#9-system-design-interview-playbook--capacity-math)
10. [The Architect's Toolkit, Cost Modeling & Bookmarks](#10-the-architects-toolkit-cost-modeling--bookmarks)
11. [What the Final Class Measured](#11-what-the-final-class-measured)
12. [The Other Files in This Folder](#12-the-other-files-in-this-folder)

---

## 1. The Architect's Mindset & Operating Model

### 1.1 Architecture vs. Design vs. Implementation
- **Architecture**: Fundamental structural decisions with high cost of reversal (e.g. database-per-service vs shared DB, synchronous RPC vs event backbone, relational vs document storage, tenancy isolation model).
- **Design**: Component-level structuring within architectural boundaries (e.g. design patterns, vertical slice layout, repository abstractions, factory patterns).
- **Implementation**: The concrete language code and configuration (e.g. C# 14, EF Core mappings, Serilog configuration, Docker Compose).

### 1.2 The "Architect's Sequence" (Design-First)
Never jump directly into coding a complex system. Follow the 5-step sequence:
1. **Context & Forces**: What technical, organizational, or scaling bottleneck prompted this change?
2. **Options Matrix**: Identify at least 3 distinct architectural options (e.g., Sync HTTP bridge vs Kafka Outbox vs 2PC distributed transaction).
3. **Trade-offs & Failure Modes**: How does each option fail in production? What is the blast radius?
4. **Draft the ADR**: Capture the decision using the Nygard template (`Context`, `Decision`, `Consequences`, `Trade-offs`, `Revisit When`, `Cross-Stack Equivalents`).
5. **Code as Proof**: Write code and automated tests (e.g. Boundary tests) to prove the design functions under stress.

```
┌─────────────────┐     ┌────────────────┐     ┌───────────────┐     ┌──────────────────┐
│ 1. Problem &    │ ──> │ 2. Options     │ ──> │ 3. Failure    │ ──> │ 4. ADR Accepted  │
│    Forces       │     │    Trade-offs  │     │    Mode Audit │     │    (Revisit When)│
└─────────────────┘     └────────────────┘     └───────────────┘     └────────┬─────────┘
                                                                              │
                                                                              ▼
                                                                     ┌──────────────────┐
                                                                     │ 5. Code & Tests  │
                                                                     │    (The Proof)   │
                                                                     └──────────────────┘
```

### 1.3 Conway's Law & Team Topologies
- **Conway's Law**: *"Organizations which design systems are constrained to produce designs which are copies of the communication structures of these organizations."*
- **Inverse Conway Maneuver**: Evolve team structures to match your target software architecture (e.g. create dedicated stream-aligned teams around `Ordering`, `Restaurant`, `Delivery`, `Payment` before extracting services).
- **Team Types (Team Topologies)**:
  - *Stream-Aligned Team*: Aligned to a continuous flow of business work (e.g. Checkout flow).
  - *Platform Team*: Provides underlying platforms enabling stream teams (e.g. Kafka/Observability platform).
  - *Complicated-Subsystem Team*: Deep specialized domain (e.g. Route optimization engine, cryptographic payment vault).
  - *Enabling Team*: Upskills teams on new capabilities (e.g. Architecture coaching, AI-assisted tooling).

### 1.4 Architecture Fitness Functions
Architecture cannot rely on team goodwill or verbal agreements. It must be mechanically enforced through automated fitness functions:
- **Boundary Tests**: Unit tests asserting no cross-schema foreign keys (ADR-008) and no illegal project references (e.g., Ordering never references Payment).
- **Static Analysis**: Linter rules preventing direct SQL calls outside data repositories.
- **Contract Tests**: Consumer-Driven Contracts (Pact) ensuring API backwards compatibility.
- **Chaos Verification**: Automated tests killing dependencies to prove circuit breakers and fallbacks operate correctly.

---

## 2. Architectural Styles & Evolution Mechanics

### 2.1 The Evolution Spectrum

| Dimension | Monolith | Modular Monolith | Microservices | Event-Driven (EDA) |
| :--- | :--- | :--- | :--- | :--- |
| **Deployment Unit** | 1 single artifact | 1 single artifact | $N$ independent artifacts | $N$ event producers/consumers |
| **Data Boundary** | 1 shared database schema | Schema-per-domain in 1 DB | Database-per-service ($N$ DBs) | Event logs + materialized read models |
| **Communication** | In-memory method calls | In-memory MediatR / Channels | HTTP REST / gRPC | Kafka / RabbitMQ events |
| **Failure Blast Radius** | Entire process crashes | Entire process crashes | Isolated to single service | Completely decoupled asynchronously |
| **Operational Overhead**| Minimal (1 pipeline) | Low (1 pipeline + linters) | High (k8s, mesh, tracing, YARP) | High (schema registry, lag watch) |
| **Team Scalability** | Low (merge bottlenecks) | Moderate (clear domain boundaries) | High (autonomous deployments) | Very High (decoupled lifecycles) |

### 2.2 When to Break the Monolith (The 4 Valid Triggers)
Never extract a microservice for "hype" or resume-driven development. Split **only** when driven by:
1. **Organizational Scale (Conway's Law)**: 20+ engineers constantly conflicting on git merges, deployments, and domain ownership (e.g. Day 12 Restaurant extraction).
2. **Different Scaling / Durability Profiles**: One domain needs 50,000 req/sec with in-memory caching while another handles 10 write req/sec with heavy ACID storage (e.g. Day 11 Delivery extraction).
3. **Fault & Blast Radius Isolation**: A failure in payment processing or external gateways must never take down menu browsing or order intake (e.g. Day 8 Payment extraction).
4. **Security & Regulatory Compliance (PCI-DSS / HIPAA)**: Isolating credit card handling into a locked-down, auditable boundary to reduce compliance audit costs.

### 2.3 The Strangler Fig & Expand-Contract Patterns
- **Strangler Fig**: Incrementally intercept routes at an API Gateway (YARP / Envoy) and route traffic from legacy monolith endpoints to new microservices until the monolith disappears.
- **Expand-Contract (Parallel Run)**:
  1. *Expand*: Add the new field / schema / service alongside the old one.
  2. *Migrate / Dual-Write*: Write to both old and new. Backfill historical records in chunked batches with `FOR UPDATE SKIP LOCKED`.
  3. *Contract*: Flip the read switch to the new structure, deprecate, and drop the legacy structure.

---

## 3. Distributed Systems Patterns & Messaging

### 3.1 The Dual-Write Problem
When an application needs to update a database AND send a message to a broker (e.g., save order to Postgres and send `OrderPlaced` to Kafka), network failures guarantee inconsistency:
- If you write to DB first and broker fails $\rightarrow$ Message lost forever.
- If you publish to broker first and DB fails $\rightarrow$ Phantom message processed for non-existent order.
- **Why 2PC / XA is Dead**: Two-Phase Commit introduces extreme latency, blocking locks, single point of coordinator failure, and is not supported by modern cloud brokers (Kafka, RabbitMQ, SQS).

### 3.2 The Transactional Outbox & Inbox Pattern (ADR-028)
- **The Outbox Fix**: Write the domain entity AND an Outbox event row inside the **SAME atomic database transaction**.
- **The Relay**: A background processor queries the outbox table using `SELECT ... FOR UPDATE SKIP LOCKED`, publishes to Kafka, and marks the message dispatched.
- **The Inbox (Deduplication)**: Consumers store processed message IDs in an `inbox` table with a unique constraint. If a duplicate arrives, it is discarded immediately.
- **Guaranteed Outcome**: **At-Least-Once Delivery + Idempotent Processing = Exactly-Once Effect**.

```
[Client] ──> [API Controller]
                    │
                    ▼
          ┌───────────────────────────────────┐
          │     Postgres Transaction          │
          │  1. INSERT INTO ordering.orders   │
          │  2. INSERT INTO ordering.outbox   │
          └─────────────────┬─────────────────┘
                            │ (Atomic Commit)
                            ▼
                    [Outbox Table]
                            │
                            ▼
          [OutboxRelay (SKIP LOCKED)] ──> [Kafka Topic: order-placed]
                                                      │
                                                      ▼
                                           [Consumer + Inbox Dedup]
```

### 3.3 Distributed Sagas (Choreography vs. Orchestration)
When a business process spans multiple services without 2PC:

| Dimension | Choreography (Tadka Day 9) | Orchestration (Temporal / Camunda) |
| :--- | :--- | :--- |
| **Coordination** | Decentralized via events | Centralized coordinator process |
| **Coupling** | Loose (participants listen to domain events) | Tighter (coordinator knows all participants) |
| **Visibility** | Harder to trace without distributed tracing | Complete state machine dashboard |
| **Best For** | 2 to 4 services, event-native systems | 5+ services, complex human-in-the-loop workflows |

- **Compensating Transactions**: If a step fails (e.g. Payment declined), a backward compensating transaction must be executed (e.g. a restaurant rejects a paid order, so the order is cancelled and the payment is refunded, ADR-045).

### 3.4 Event-Carried State Transfer (ECST) & Local Read Models (ADR-037)
- **The Problem**: Service A (Ordering) needs data owned by Service B (Restaurant menu prices) during checkout. Calling B via HTTP creates **temporal coupling**, if Restaurant goes down, Ordering goes down!
- **The Pattern**: Service B publishes full-snapshot event `menu-updated` whenever prices change. Service A consumes the event and updates its own local read replica table (`ordering.menu_replica`).
- **The Result**: Ordering calculates prices locally with zero external network dependencies. Restaurant can be completely offline, yet orders continue to process seamlessly.

---

## 4. Data Architecture, Storage & Persistence

### 4.1 Polyglot Persistence Decision Matrix

```
                               ┌────────────────────────────────┐
                               │ What is your primary data need?│
                               └───────────────┬────────────────┘
                                               │
         ┌───────────────────────┬─────────────┴──────────┬────────────────────────┐
         ▼                       ▼                        ▼                        ▼
┌──────────────────┐    ┌──────────────────┐    ┌──────────────────┐    ┌──────────────────┐
│ Relational ACID  │    │ In-Memory Speed  │    │ Ordered Stream   │    │ Full-Text Search │
│ - Ledger/Money   │    │ - Cache-aside    │    │ - Immutable log  │    │ - Fuzzy queries  │
│ - Strict Schemas │    │ - SSE Pub/Sub    │    │ - Event replay   │    │ - Faceted browse │
│ - Complex Joins  │    │ - Geo location   │    │ - Distributed log│    │ - Log indexing   │
└────────┬─────────┘    └────────┬─────────┘    └────────┬─────────┘    └────────┬─────────┘
         ▼                       ▼                        ▼                        ▼
    PostgreSQL                 Redis                    Kafka                 Elasticsearch
```

### 4.2 Caching Strategies & The 3 Cache Disasters
- **Cache-Aside (Lazy Loading)**: Read from cache $\rightarrow$ if miss, read from DB $\rightarrow$ write to cache with TTL $\rightarrow$ return.
- **The 3 Disasters & How Architects Prevent Them**:
  1. **Cache Stampede (Thundering Herd)**: Hot key expires; 5,000 concurrent requests all miss cache and hit the database simultaneously.
     - *Antidote*: **Single-Flight Lock (Mutex)** (ADR-019). Only the first thread queries DB; the remaining 4,999 await that thread's result.
  2. **Cache Penetration**: Malicious actor requests non-existent IDs (`id=-9999`); queries bypass cache and hammer database.
     - *Antidote*: **Bloom Filter** at edge or cache null objects with a short TTL (e.g. 60 seconds).
  3. **Cache Avalanche**: Hundreds of keys expire at the exact same minute.
     - *Antidote*: **TTL Jitter** (e.g., base TTL 10 min + `Random(0, 120s)`).

### 4.3 Concurrency Control: Optimistic vs. Pessimistic
- **Optimistic Concurrency Control (OCC)**:
  - Assume conflicts are rare. Read without locks, assert version token on write (`UPDATE orders SET status = 'Paid' WHERE id = @id AND xmin = @originalVersion`).
  - If 0 rows updated $\rightarrow$ Throw 409 Conflict. Client retries.
  - *Best for*: High-read web APIs, e-commerce checkout.
- **Pessimistic Locking**:
  - Acquire row-level lock immediately (`SELECT * FROM orders WHERE id = @id FOR UPDATE`).
  - Prevents anyone else from reading/modifying until transaction commits.
  - *Best for*: Financial reconciliation batches, inventory reservation under extreme contention.

### 4.4 Connection Pool Sizing (Little's Law)
- Never set database connection pools to 500 blindly.
- **Formula**: `Connections = (Target QPS * Average Query Duration in seconds) + Buffer`
- In PostgreSQL, excessive connections cause context-switching overhead, memory thrashing, and lock contention. 20–50 tuned connections often out-perform 500 unconstrained connections (ADR-015).

---

## 5. Resilience, Fault Tolerance & Chaos Engineering

### 5.1 Michael Nygard's 4 Pillars of Stability (Release It!)

```
[Inbound Request] ──> [Bulkhead (Max 10 Concurrent)]
                              │
                              ▼
                      [Retry (2 retries, Jittered Backoff, Transport Only)]
                              │
                              ▼
                      [Circuit Breaker (Ratio 0.5, min 5 calls, 30 s window, 60 s open)]
                              │
                              ▼
                      [Timeout (2.0s per attempt)] ──> [External Gateway]

(This is the order in `PaymentResiliencePipeline.cs`: bulkhead, then retry, then breaker, then timeout.)
```

1. **Timeouts**: Every external call must have a hard deadline (e.g., 2.0s). Never rely on default framework timeouts (which are often 100 seconds in .NET `HttpClient` or infinite in raw sockets).
2. **Retries with Exponential Backoff + Jitter**:
   - Retry with constant delay causes resonance spikes.
   - **Full Jitter Formula**: `delay = Random(0, Min(max_delay, base_delay * 2^attempt))`
3. **Circuit Breakers (ADR-043)**:
   - State machine: **CLOSED** (normal) $\rightarrow$ **OPEN** (fail-fast immediately without network call) $\rightarrow$ **HALF-OPEN** (probe canary request).
   - *Key Knob*: Must enforce a `MinimumThroughput` (e.g. 5 requests) so a single isolated test failure doesn't trip the breaker.
4. **Bulkheads**: Isolate thread pools or concurrency slots. If the payment gateway hangs, it can hold at most 10 concurrent charges, so the rest of the service keeps serving menu browsing and login.

### 5.2 Exception Predicates & Dependency Classification (ADR-043, ADR-044)
- **The Fatal Flaw**: Retrying on a 400 Bad Request or 402 Card Declined.
  - A business decline is final. Retrying causes double charges and trips the circuit breaker on valid business outcomes.
  - **Rule**: Retries and Circuit Breakers MUST ONLY catch **Transport Exceptions** (in Tadka: `TimeoutRejectedException` and `PaymentGatewayUnavailableException`). A decline is a `PaymentDeclinedException`: it is final, never retried, never counted by the breaker.
- **Dependency Classification**:
  - **Correctness Dependencies** (Postgres, Money, Ledger): Must fail honestly. Never fabricate state or return stale financial balances.
  - **Performance Dependencies** (Redis Cache, Search Index): Degrade gracefully. If Redis is down, fall through to Postgres; never return 500 to a user browsing a menu.

---

## 6. Observability & Distributed Telemetry

### 6.1 The 3 Pillars of Observability

```
                ┌──────────────────────────────────┐
                │ Observability Pillars in Tadka   │
                └────────────────┬─────────────────┘
                                 │
         ┌───────────────────────┼───────────────────────┐
         ▼                       ▼                       ▼
┌──────────────────┐    ┌──────────────────┐    ┌──────────────────┐
│ Distributed Trace│    │ Structured Logs  │    │ Aggregated Metric│
│ (Jaeger / OTel)  │    │ (Serilog / OTel) │    │ (Prometheus)     │
│ - W3C traceparent│    │ - JSON format    │    │ - RED Method     │
│ - Outbox / Kafka │    │ - trace_id injected│  │ - Low cardinality│
└──────────────────┘    └──────────────────┘    └──────────────────┘
```

### 6.2 Trace Context Propagation Across Boundaries (ADR-041)
Distributed traces break when crossing asynchronous boundaries unless explicitly forwarded:
1. **HTTP**: Injected as W3C header `traceparent: 00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01`.
2. **Transactional Outbox**: Save `TraceParent` column in the database table when enqueuing the event.
3. **Kafka Relay**: Read the column, create a publish span, and inject `traceparent` as a Kafka record header.
4. **Consumer**: Extract the header, create a child span, and execute consumer logic. Result: **A single continuous trace across 4 services and the message bus**.

### 6.3 The Law of Metric Cardinality (ADR-042)
- **High Cardinality**: Attributes with unbounded unique values (`order_id`, `user_id`, `guid`, `email`).
- **Low Cardinality**: Attributes with bounded values (`service_name`, `status_code`, `payment_method`).
- **The Rule**: **NEVER put high-cardinality attributes in Prometheus metric labels**. It causes TSDB memory explosion and crashes monitoring infrastructure. Put high-cardinality values in trace span attributes or structured logs.

---

## 7. Security Architecture & Zero Trust

### 7.1 Zero Trust in Microservices (ADR-031)
- **The Antipattern**: Treating the API Gateway as a "secure perimeter" and having internal services trust all unauthenticated HTTP traffic behind the gateway.
- **The Solution**: Every internal microservice validates the incoming JWT signature, expiration, issuer, and claims independently. If an attacker breaches the gateway or a pod, lateral movement is blocked.
- **Hardening added after Day 14**: login rate limiting and account lockout (ADR-065), single-use refresh tokens with reuse detection (ADR-066), RS256 signing with a JWKS endpoint so keys can rotate (ADR-067), field-level encryption of the phone number (ADR-052) and card tokenization (ADR-053).

### 7.2 Claim Mapping & Resource Ownership
- **Claim Mapping**: By default, ASP.NET Core maps standard JWT `role` claims to legacy XML SOAP namespaces. Setting `MapInboundClaims = false` ensures `[Authorize(Roles = "Admin")]` inspects the literal `role` claim.
- **Resource Ownership Validation**: Never trust client-submitted IDs in request bodies alone:
  ```csharp
  // Enforce Resource Ownership (as written in OrdersController)
  if (!User.IsAdmin() && order.CustomerId != User.UserId())
      return Forbid(); // 403
  ```

### 7.3 PII Masking & GDPR Right-to-be-Forgotten (ADR-032)
- Mask sensitive data in logs (`PiiMasker` destructuring).
- On GDPR deletion request: Anonymize customer names, emails, and phone numbers in historical records while preserving non-PII financial ledger entries for tax and accounting compliance.

---

## 8. Visual Modeling & Architecture Diagrams (C4 Model)

The industry standard for communicating software architecture is Simon Brown's **C4 Model**:
- **Level 1: System Context**: The system, external users, and external integrations.
- **Level 2: Container Diagram**: High-level execution units (Web APIs, Mobile apps, Databases, Kafka broker, Caches).
- **Level 3: Component Diagram**: Internal modular structure of a container (Controllers, Services, Outbox relay, Repositories).
- **Level 4: Code Diagram**: UML class or entity diagrams (usually generated on demand).

> For full interactive Mermaid.js diagrams of all Tadka architectures, open [DIAGRAMS.md](DIAGRAMS.md).

---

## 9. System Design Interview Playbook & Capacity Math

### 9.1 The 45-Minute Pacing Blueprint
1. **00–05 min: Clarify Scope & Functional / Non-Functional Requirements (NFRs)**
   - What is in scope? What is explicitly out of scope?
   - What are the throughput, p99 latency, durability, and availability expectations?
2. **05–10 min: Back-of-the-Envelope Capacity Estimations**
   - Calculate Read QPS, Write QPS, Storage per year, Network Bandwidth, and RAM cache requirements.
3. **10–20 min: High-Level Architecture (HLD)**
   - Draw Client $\rightarrow$ CDN/DNS $\rightarrow$ API Gateway $\rightarrow$ Services $\rightarrow$ Cache $\rightarrow$ DB $\rightarrow$ Message Bus.
4. **20–35 min: Deep-Dive into Core Bottlenecks**
   - Address the specific hard problem (e.g. Dual-write, payment idempotency, geo-spatial driver assignment, cache stampede).
5. **35–45 min: Resilience, Trade-offs & Failure Modes**
   - Walk through what happens when Redis dies, when the payment gateway brownouts, or when network partitions occur.

### 9.2 Latency Numbers Every Architect Must Know

| Operation | Approximate Latency | Human-Scale Analogy (1 cycle = 1 sec) |
| :--- | :--- | :--- |
| **L1 CPU Cache reference** | 0.5 ns | 1 second |
| **L2 CPU Cache reference** | 7 ns | 14 seconds |
| **RAM access** | 100 ns | 3.3 minutes |
| **Read 1 MB sequentially from NVMe SSD** | 250,000 ns (250 µs) | 5.8 days |
| **Read 1 MB sequentially from Network (1 Gbps)** | 10,000,000 ns (10 ms) | 8 months |
| **Send packet Mumbai to London & back** | 120,000,000 ns (120 ms) | 8 years |

### 9.3 Quick Capacity Estimation Cheat Sheet
- **QPS from Daily Active Users (DAU)**:
  $$\text{Average QPS} = \frac{\text{DAU} \times \text{Requests per user per day}}{86,400 \text{ seconds}}$$
  $$\text{Peak QPS} = \text{Average QPS} \times 2 \text{ to } 5$$
  - *Example*: 10 Million DAU, 10 reads/day:
    $$\text{Avg QPS} = \frac{100,000,000}{86,400} \approx 1,157 \text{ QPS} \implies \text{Peak QPS} \approx 3,500 \text{ to } 5,000 \text{ QPS}$$
- **Storage Growth per Year**:
  $$\text{Daily Storage} = \text{Write QPS} \times 86,400 \times \text{Payload Size}$$
  $$\text{Annual Storage} = \text{Daily Storage} \times 365$$
- **80/20 RAM Cache Rule**:
  - 20% of content generates 80% of read traffic.
  - Cache size = $0.20 \times \text{Daily Read Data Volume}$.

---

## 10. The Architect's Toolkit, Cost Modeling & Bookmarks

### 10.1 Cloud Cost Modeling (the Rs AWS-Mumbai model)
An architect does not just design systems; an architect designs systems **they can afford to operate**. The numbers come from `docs/cost-model.md`. They are a **model, not a bill** (ADR-039). The live Azure session (ADR-064) is measured separately.

| Design | Rs per month at 1 lakh orders/day | What it is |
| :--- | :--- | :--- |
| Honest minimal monolith | **~6,100** | One app server, one modest Postgres, one Redis |
| The system we built (4 services, 4 databases, replica, Kafka, Redis, load balancer, observability) | **~31,300** | About **5 times** the honest minimum |
| The same system at 10 lakh orders/day | **~88,000** | Ten times the traffic, about 2.8 times the cost |

The lesson is the honest trade-off: **operational independence is not free**. At low volume the split costs about 5 times more, because every service brings its own database and the Kafka brokers sit mostly idle. The cost grows slower than the traffic, so the gap closes as volume grows. Split because of fault, compliance, scaling or team reasons (section 2.2), never because it is cheaper.

Per 1,000 orders: Rs 6,100 / 3,000,000 x 1,000 = about **Rs 2.03** for the monolith, and Rs 31,300 / 3,000,000 x 1,000 = about **Rs 10.43** for the distributed system at 1 lakh orders/day. At 10 lakh orders/day the distributed system is Rs 88,000 / 30,000,000 x 1,000 = about **Rs 2.93**.

### 10.2 The Essential Software Architecture Bookshelf
1. **Designing Data-Intensive Applications**, Martin Kleppmann *(The bible of distributed data systems)*
2. **Release It! (2nd Edition)**, Michael T. Nygard *(Resilience, circuit breakers, bulkheads, stability patterns)*
3. **Building Microservices (2nd Edition)**, Sam Newman *(Service decomposition, boundaries, migrations)*
4. **Enterprise Integration Patterns**, Gregor Hohpe & Bobby Woolf *(Messaging, pipes, filters, outbox/inbox)*
5. **Team Topologies**, Matthew Skelton & Manuel Pais *(Conway's Law, team organization for architecture flow)*
6. **Software Architecture: The Hard Parts**, Neal Ford, Mark Richards, Pramod Sadalage, Zhamak Dehghani *(Trade-off analysis)*

---

---

## 11. What the Final Class Measured

On a laptop, with the five services running and k6 as the load generator (details in `docs/runbooks/day-16.md`):

- **The HTTP side did not break.** Up to 300 virtual users (about 300 requests per second), menu p99 stayed near 5 ms, with no errors.
- **The queue did.** `POST /orders` returns 201 as soon as the order and its outbox row are saved. The charge happens later, from Kafka. Payment drains about **4.4 orders per second** (one partition, one consumer). Faster than that, the `tadka-payment` consumer lag grows and new orders sit in `Created`.
- **The arithmetic.** The design load is about 1.2 orders per second on average and about 25 per second at burst. A one-minute burst at 25 per second leaves about 1,200 orders waiting, and clearing them takes about 6 minutes.
- **The lesson.** A fast API can hide a slow system. Watch the **queue lag**, not only p99. The fix is architectural: more partitions and more consumers (and then measure again).
- **The honest limits.** A closed-loop test (users who wait for a reply) understates real traffic, so production tests use an arrival rate (runbook section 7). Front Door, WAF and CDN were never run live (they need a paid Azure account).

---

## 12. The Other Files in This Folder

| File | Use it for |
| :--- | :--- |
| [ADR_INDEX.md](ADR_INDEX.md) | Every decision, 001 to 067, with the day and a one-line reason |
| [PATTERNS_CATALOG.md](PATTERNS_CATALOG.md) | 25 core patterns plus the hardening patterns, each with intent, mechanics and the trap |
| [DIAGRAMS.md](DIAGRAMS.md) | Seven Mermaid diagrams: the system, the saga, the outbox, resilience, read models, tracing, containers |
| [SYSTEM_DESIGN_CHEATSHEET.md](SYSTEM_DESIGN_CHEATSHEET.md) | Latency numbers, capacity math, the 45-minute interview plan, decision trees, Tadka's own numbers |
| [../README.md](../README.md) | Working with AI coding tools on Tadka |

*Graduation Note to Cohort Members:*  
Architecture is not about memorizing buzzwords; it is about knowing the **trade-offs**, predicting the **failure modes**, and having the discipline to make decisions that stand the test of production scale. Go build resilient systems!
