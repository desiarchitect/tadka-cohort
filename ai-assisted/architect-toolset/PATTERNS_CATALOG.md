# The Architect's Design Patterns Catalog (25 Core Patterns)

> **Desi Architect Cohort, Architecture Patterns Reference**  
> Every pattern is documented with its Intent, Mechanics, Failure Modes, and Tadka ADR mapping.

---

## Index of Patterns
1. [Transactional Outbox](#1-transactional-outbox-pattern)
2. [Transactional Inbox (Idempotent Consumer)](#2-transactional-inbox-idempotent-consumer)
3. [Saga Choreography](#3-saga-choreography-pattern)
4. [Event-Carried State Transfer (ECST)](#4-event-carried-state-transfer-ecst)
5. [Cache-Aside (Lazy Loading)](#5-cache-aside-lazy-loading)
6. [Single-Flight Lock (Cache Stampede Mutex)](#6-single-flight-lock-cache-stampede-mutex)
7. [Circuit Breaker](#7-circuit-breaker-pattern)
8. [Bulkhead Isolation](#8-bulkhead-isolation-pattern)
9. [Jittered Exponential Backoff Retry](#9-jittered-exponential-backoff-retry)
10. [Strangler Fig Pattern](#10-strangler-fig-pattern)
11. [Expand-Contract (Parallel Run) Migration](#11-expand-contract-parallel-run-migration)
12. [Schema-per-Domain](#12-schema-per-domain-pattern)
13. [Database-per-Service](#13-database-per-service-pattern)
14. [Read-Write Replica Split](#14-read-write-replica-split)
15. [Optimistic Concurrency Control (OCC)](#15-optimistic-concurrency-control-occ)
16. [Value Object (DDD)](#16-value-object-pattern)
17. [API Gateway & Path Routing](#17-api-gateway--path-routing-yarp)
18. [Edge Rate Limiting (Fixed Window)](#18-edge-rate-limiting-fixed-window)
19. [Distributed Trace Context Propagation](#19-distributed-trace-context-propagation)
20. [Low-Cardinality Telemetry Design](#20-low-cardinality-telemetry-design)
21. [Zero-Trust Internal Authentication](#21-zero-trust-internal-authentication)
22. [Server-Sent Events (SSE) with Redis Pub/Sub](#22-server-sent-events-sse-with-redis-pubsub)
23. [Polyglot Geo-Spatial Tracking](#23-polyglot-geo-spatial-tracking-redis-geo)
24. [Architecture Boundary Fitness Tests](#24-architecture-boundary-fitness-tests)
25. [RFC 7807 Problem Details (API Error Model)](#25-rfc-7807-problem-details-api-error-model)

---

### 1. Transactional Outbox Pattern
- **ADR Reference:** ADR-028
- **Intent:** Atomically update database state and publish events to a message broker without dual-write inconsistency or 2PC.
- **Mechanics:** Inside the business database transaction, insert an event record into an `outbox` table. A background worker queries the outbox using `SELECT ... FOR UPDATE SKIP LOCKED`, dispatches to Kafka, and marks the record dispatched.
- **Failure Mode & Trap:** Missing `SKIP LOCKED` causes concurrent relay worker instances to claim and publish the exact same message batch twice.
- **Cross-Stack:** MassTransit / NServiceBus (.NET), Debezium / transactional-outbox (Java/Spring), Watermill (Go).

---

### 2. Transactional Inbox (Idempotent Consumer)
- **ADR Reference:** ADR-028
- **Intent:** Guarantee that duplicate message deliveries from at-least-once brokers do not result in duplicate state mutations.
- **Mechanics:** Consumers record the unique `MessageId` in an `inbox` table with a unique primary key constraint before executing the state mutation. If a primary key violation occurs, the message is acknowledged and skipped.
- **Failure Mode & Trap:** Recording message completion in memory instead of durable storage; restarts cause duplicate processing of replayed broker logs.

---

### 3. Saga Choreography Pattern
- **ADR Reference:** ADR-029
- **Intent:** Manage distributed transactions across decoupled microservices without a centralized coordinator or two-phase commit.
- **Mechanics:** Each participating service listens to domain events (e.g. `order-placed`), executes its local transaction, and publishes a subsequent event (`payment-results`). If a step fails, compensating transactions are published to undo preceding steps.
- **Failure Mode & Trap:** Cyclic event loops if event naming is imperative instead of past-tense facts (use `OrderPlaced`, never `PlaceOrderCommand`).

---

### 4. Event-Carried State Transfer (ECST)
- **ADR Reference:** ADR-037
- **Intent:** Eliminate synchronous inter-service HTTP calls on high-frequency read or checkout paths.
- **Mechanics:** The upstream system publishes full snapshot events whenever data changes (`menu-updated`). Downstream consumers maintain their own local read replica tables (`ordering.menu_replica`) and perform in-process joins.
- **Failure Mode & Trap:** Eventual consistency lag; if a price updates, orders placed in the 50ms window before event arrival use the prior snapshot (mitigated by server-side validation or price quote tokens).

---

### 5. Cache-Aside (Lazy Loading)
- **ADR Reference:** ADR-018
- **Intent:** Optimize read throughput and reduce relational database load for read-heavy workloads.
- **Mechanics:** Application checks Redis. On cache hit, return cached data. On cache miss, read from PostgreSQL, populate Redis with a TTL (e.g., 10 minutes), and return.
- **Failure Mode & Trap:** Stale reads after database writes. Must implement delete-on-write cache invalidation on any entity mutation.

---

### 6. Single-Flight Lock (Cache Stampede Mutex)
- **ADR Reference:** ADR-019
- **Intent:** Prevent thousands of concurrent requests from hammering the database when a popular cache key expires.
- **Mechanics:** When a cache miss occurs, threads attempt to acquire an in-process `SemaphoreSlim` or Redis distributed lock for that specific key. Only the winning thread queries the DB; other threads await the result.
- **Failure Mode & Trap:** Forgetting lock timeouts; if the DB query deadlocks, waiting threads become stuck indefinitely.

---

### 7. Circuit Breaker Pattern
- **ADR Reference:** ADR-043
- **Intent:** Prevent cascading failures and avoid repeatedly calling a failing or hung external dependency.
- **Mechanics:** Evaluates failure rates over a rolling window. When failures exceed a threshold (e.g. 50% across $\ge 5$ requests), the breaker trips to **OPEN**, immediately failing requests without hitting the network. After a cooldown (e.g. 60s), it enters **HALF-OPEN** to test canary requests.
- **Failure Mode & Trap:** Catching business validation exceptions (e.g. card declined) in the circuit breaker predicate, causing valid user declines to trip the breaker for everyone.

---

### 8. Bulkhead Isolation Pattern
- **ADR Reference:** ADR-021
- **Intent:** Isolate system resources so that a failure in one external dependency cannot starve or crash the entire application.
- **Mechanics:** Limit maximum concurrent outbound executions (e.g. max 10 concurrent calls to Payment Gateway). Excess requests fail fast or queue with a timeout.
- **Failure Mode & Trap:** Setting bulkhead capacity higher than the underlying HTTP connection pool size.

---

### 9. Jittered Exponential Backoff Retry
- **ADR Reference:** ADR-043
- **Intent:** Allow transient network glitches to recover without creating destructive resonance waves.
- **Mechanics:** On transient transport failure, wait longer after each attempt (the delay roughly doubles from a short base) plus a random jitter component: `Random(0, Min(maxDelay, baseDelay * 2^attempt))`.
- **Failure Mode & Trap:** Retrying non-idempotent operations (like credit card charges) without an idempotency key.

---

### 10. Strangler Fig Pattern
- **ADR Reference:** ADR-035
- **Intent:** Migrate legacy monolith capabilities to microservices incrementally with zero downtime.
- **Mechanics:** Place an API Gateway (YARP) in front of the monolith. As a new service is built (e.g. `Payment.Api`), update gateway routing rules to direct `/api/v1/payments/**` to the new service while keeping all other traffic directed to the monolith.
- **Failure Mode & Trap:** Shared database coupling underneath the gateway; services appear separate at the HTTP layer but deadlock each other at the database layer.

---

### 11. Expand-Contract (Parallel Run) Migration
- **ADR Reference:** ADR-038
- **Intent:** Evolve database schemas or API contracts with zero downtime and zero deployment locks.
- **Mechanics:**
  1. *Expand*: Add the new column/table as nullable.
  2. *Backfill*: Background worker backfills historical data in batches using `FOR UPDATE SKIP LOCKED`.
  3. *Contract*: Once all rows are populated, mark column NOT NULL and drop the old column.
- **Failure Mode & Trap:** Running a synchronous `ALTER TABLE ... SET NOT NULL` on a table with 10 million rows without pre-populating, which locks the table for minutes.

---

### 12. Schema-per-Domain Pattern
- **ADR Reference:** ADR-003, ADR-008
- **Intent:** Enforce modular domain boundaries in a monolith before physically extracting microservices.
- **Mechanics:** Maintain one physical PostgreSQL database, but create distinct logical schemas (`ordering`, `restaurant`, `delivery`, `identity`, `payment`). Cross-schema foreign keys are strictly prohibited.
- **Failure Mode & Trap:** Developers writing raw SQL joins across schemas (`JOIN restaurant.menu_items`), destroying future microservice extraction feasibility.

---

### 13. Database-per-Service Pattern
- **ADR Reference:** ADR-026
- **Intent:** Achieve true runtime, scaling, and fault isolation between microservices.
- **Mechanics:** Each extracted microservice owns its physical database instance or logical database with separate credentials. No other service may connect to that database.
- **Failure Mode & Trap:** Attempting distributed joins across services over synchronous HTTP REST queries.

---

### 14. Read-Write Replica Split
- **ADR Reference:** ADR-016
- **Intent:** Offload high-volume read traffic (menu browsing, order history) from the primary transactional database.
- **Mechanics:** PostgreSQL primary handles writes and streams changes via WAL replication to a read replica. In Tadka, `TadkaDbContext` writes to the primary and `TadkaReadDbContext` serves reads from the replica. A read that must see a fresh write stays on the primary.
- **Failure Mode & Trap:** Replication lag; a user creates an order, is redirected to `GET /orders/{id}`, queries the replica before replication finishes, and receives a 404 (Solve by reading newly created resources from primary or cache).

---

### 15. Optimistic Concurrency Control (OCC)
- **ADR Reference:** ADR-012
- **Intent:** Detect concurrent modifications to shared state without holding database row locks.
- **Mechanics:** Use PostgreSQL's internal `xmin` system column or an integer `Version` column. Updates verify `WHERE id = @id AND xmin = @expectedVersion`. If 0 rows updated, return HTTP 409 Conflict.
- **Failure Mode & Trap:** Forgetting to return RFC 7807 Conflict and letting raw database exceptions bubble up as 500 Internal Server Errors.

---

### 16. Value Object Pattern (Domain-Driven Design)
- **ADR Reference:** none of its own (see the owned types in the EF configurations)
- **Intent:** Model domain concepts that have no conceptual identity, defined purely by their attributes (Money, Address, GeoCoordinates).
- **Mechanics:** Implemented as immutable C# `record` types and mapped in EF Core using `builder.OwnsOne()`. Equality is based on structural values, not IDs.
- **Failure Mode & Trap:** Making value objects mutable, causing accidental side-effects across shared references.

---

### 17. API Gateway & Path Routing (YARP)
- **ADR Reference:** ADR-035
- **Intent:** Provide a unified entry point, SSL termination, path rewriting, and rate limiting for external clients.
- **Mechanics:** YARP (Yet Another Reverse Proxy) routes incoming paths (`/deliveries/**` $\rightarrow$ Delivery service, `/api/v1/payments/**` $\rightarrow$ Payment service) with path transformation and health checks.
- **Failure Mode & Trap:** Putting heavy business logic or domain orchestration inside the API gateway (The "Smart Gateway, Dumb Endpoints" anti-pattern).

---

### 18. Edge Rate Limiting (Fixed Window)
- **ADR Reference:** ADR-035 (gateway), ADR-049 (shared counters)
- **Intent:** Protect backend services from DDoS, brute-force attacks, and rogue scrapers.
- **Mechanics:** Middleware counts requests per client IP in a fixed one-minute window (the Tadka gateway default is 120 per minute). Over the limit, it returns HTTP 429 Too Many Requests. A token bucket or a sliding window smooths bursts at the window edge, and ADR-049 makes the algorithm selectable on the monolith.
- **Failure Mode & Trap:** Counters kept in one server's memory stop being correct once several instances run (the shared counter must live in Redis, ADR-049). The trap in tests is the opposite: a load test sends every user from one IP and hits 429 after about 120 requests, which looks like a breaking point and is not (Day 16 runbook).

---

### 19. Distributed Trace Context Propagation
- **ADR Reference:** ADR-041
- **Intent:** Trace a single user request end-to-end across multiple HTTP services, transactional outbox tables, and Kafka topics.
- **Mechanics:** W3C `traceparent` headers are injected into HTTP requests, saved to the database outbox row, forwarded in Kafka record headers, and extracted by consumers using OpenTelemetry `ActivitySource`.
- **Failure Mode & Trap:** Consumer starting a brand-new trace instead of setting the incoming Kafka header as the parent, creating fragmented, orphaned traces in Jaeger.

---

### 20. Low-Cardinality Telemetry Design
- **ADR Reference:** ADR-042
- **Intent:** Prevent Prometheus Time-Series Database (TSDB) memory exhaustion while maintaining observability.
- **Mechanics:** Metric labels are strictly bounded to small enums (`service_name`, `status_code`, `payment_method`). High-cardinality values (`order_id`, `user_id`, `guid`) are relegated to trace spans or structured logs.
- **Failure Mode & Trap:** Adding `order_id` as a label in a Prometheus counter: 1,000,000 orders creates 1,000,000 unique time series, causing Prometheus Out-Of-Memory (OOM) crash.

---

### 21. Zero-Trust Internal Authentication
- **ADR Reference:** ADR-030, ADR-031
- **Intent:** Eliminate the assumption of internal network safety.
- **Mechanics:** API Gateway forwards client JWTs in the `Authorization: Bearer` header. Downstream services validate JWT signatures, expiration, and role claims independently.
- **Failure Mode & Trap:** Downstream services skipping token verification and relying on custom headers like `X-User-Id` that can be spoofed if internal traffic is compromised.

---

### 22. Server-Sent Events (SSE) with Redis Pub/Sub
- **ADR Reference:** ADR-020, ADR-055
- **Intent:** Push real-time order status updates to client browsers without heavy WebSocket handshakes.
- **Mechanics:** A client opens a persistent connection to `GET /api/v1/orders/{id}/events`. When the order's status changes, the service publishes to a Redis channel, and every API instance subscribed to it streams the event to its own connected clients. Events carry a sequence number, the last 20 are kept in Redis, and a client that reconnects sends `Last-Event-ID` to catch up (ADR-055).
- **Failure Mode & Trap:** Keeping open connections without a periodic heartbeat comment (`: ping\n\n`), causing intermediate cloud load balancers to terminate idle connections after 60 seconds.

---

### 23. Polyglot Geo-Spatial Tracking (Redis Geo)
- **ADR Reference:** ADR-034
- **Intent:** Handle thousands of live rider GPS pings per second with fast reads of the latest position.
- **Mechanics:** Store the latest rider coordinates in Redis with `GEOADD`, and keep assignment history in PostgreSQL. In Tadka today, assignment picks the **first available rider**; it does not search by distance. A real dispatcher would add a `GEOSEARCH` for riders within a radius and rank them by distance and current load. Teach that as the next step, not as something built.
- **Failure Mode & Trap:** Running high-frequency live GPS writes (1 ping/sec per rider) against relational PostgreSQL tables, causing WAL log bloat and table lock contention.

---

### 24. Architecture Boundary Fitness Tests
- **ADR Reference:** ADR-008
- **Intent:** Prevent architectural erosion mechanically in CI/CD before pull requests can be merged.
- **Mechanics:** Unit tests (e.g. `BoundaryTests.cs`) inspect EF Core metadata or compiled assembly types to assert that no foreign keys cross schema boundaries and no illegal namespace imports exist.
- **Failure Mode & Trap:** Treating boundary violations as "technical debt" to be fixed later instead of a hard build failure.

---

### 25. RFC 7807 Problem Details (API Error Model)
- **ADR Reference:** ADR-006
- **Intent:** Provide a consistent, machine-readable JSON error format for all client APIs.
- **Mechanics:** Return JSON conforming to RFC 7807: `type` (URI), `title` (short summary), `status` (HTTP code), `detail` (human readable explanation), and `instance` (request URI path).
- **Failure Mode & Trap:** Inconsistent error responses (e.g. some endpoints returning string messages, others returning custom objects, others returning HTML stack traces).

---

## Hardening patterns from ADR-045 to ADR-067

These are real decisions in the repository, added after Day 14. Each one is a pattern worth naming in an interview.

| Pattern | ADR | One line |
| :--- | :--- | :--- |
| Compensating refund (saga completion) | 045 | A rejected paid order is cancelled and refunded |
| Stateless scale-out behind a load balancer | 047 | Three identical replicas behind nginx |
| Conditional GET (ETag and 304) and compression | 048 | Do not resend what the client already has |
| Distributed rate limiting | 049 | A shared counter in Redis, so every replica agrees |
| Additive-only event schema evolution | 050 | Add fields with defaults, never remove or rename |
| Dead-letter queue for poison messages | 051 | After repeated failures, park the message and move on |
| Field-level encryption at rest | 052 | AES-GCM on the phone number |
| Payment tokenization | 053 | The card number becomes an opaque keyed token at once |
| Edge cache and signed URLs | 054 | Cache public reads, sign private links |
| SSE reconnect replay buffer | 055 | A bounded buffer, not durability |
| Pessimistic locking for hot rows | 056 | `FOR UPDATE` where conflicts are certain, proved by a load test |
| Keyset (cursor) pagination | 057 | Stable and fast where `OFFSET` gets slow |
| Percentage feature flags | 058 | A stable hash assigns each user to a bucket |
| Backpressure (admission control) | 059 | Limit concurrent requests, refuse the rest early |
| Load shedding by priority | 060 | During an incident, drop the least important routes first |
| Weighted canary at the gateway | 061 | Send a small share of traffic to the new version |
| Replica staleness metric | 063 | Make the lag of a read model visible |
| Login throttling and lockout | 065 | Two layered controls, by IP and by account |
| Refresh-token rotation with reuse detection | 066 | A reused token means theft, so end the session |
| Asymmetric signing with key rotation (JWKS) | 067 | Verifiers fetch public keys, private keys never leave the issuer |
