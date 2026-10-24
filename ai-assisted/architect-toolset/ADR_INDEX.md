# Tadka ADR index (001 to 067)

> Every architecture decision in the repository, with the day it is taught and the one-line reason. The full text of each decision is in `docs/adrs/NNN-*.md`. There are **66 ADR files**: the number **046 was never used**. The next ADR is **068**.
>
> Always quote an ADR by **number and topic**, for example "ADR-028 transactional outbox". The titles below match the file names in the repository.

## Part 1: the spine, taught Day 1 to Day 14 (ADR-001 to ADR-044)

| ADR | Topic (file name) | Day | The decision in one line | In other stacks |
| :--- | :--- | :--- | :--- | :--- |
| 001 | `dotnet10` | 1 | Build on .NET 10 (a long-term-support release), one language for every service | Spring Boot 3 on Java 21, Go, Node |
| 002 | `monolith-first` | 1 | Start as one deployable. Split only when a real force appears | Spring Modulith |
| 003 | `schema-per-domain` | 2 | One Postgres, one schema per domain, so boundaries exist before services do | Separate schemas or databases |
| 004 | `ef-core-code-first` | 2 | Code-first EF Core with Fluent API configuration and migrations | Hibernate or JPA, GORM, Prisma |
| 005 | `rest-api-style` | 3 | REST over HTTP with explicit routes and status codes. No `PUT`, no `DELETE` on the core resources | Spring MVC, Gin, Express |
| 006 | `rfc7807-errors` | 3 | Every error is a Problem Details JSON body | Spring `ProblemDetail` |
| 007 | `two-layer-validation` | 3 | Validate at the HTTP edge, and again in the domain | Bean Validation plus domain guards |
| 008 | `no-cross-schema-fks` | 2 | No foreign key across schemas. Cross-domain links are plain ids. A test enforces it | ArchUnit, NetArchTest |
| 009 | `denormalize-order-items` | 3 | An order stores a snapshot of item name and price at order time | Embedded document |
| 010 | `api-versioning` | 3 | Version in the URL: `/api/v1`. Additive changes do not bump it | URI versioning |
| 011 | `idempotency-for-unsafe-writes` | 4 | `Idempotency-Key` header. The key is saved in the same transaction as the order | Idempotency filter, Stripe-style keys |
| 012 | `optimistic-concurrency-orders` | 4 | The `xmin` column detects a lost race and returns 409 | JPA `@Version`, ETag with `If-Match` |
| 013 | `in-process-domain-events` | 4 | Domain events dispatched in-process after the save. At-most-once, which motivates the outbox | Spring application events |
| 014 | `indexing-strategy` | 5 | Composite B-tree indexes on the ids and dates we filter by | Any relational database |
| 015 | `connection-pool-sizing` | 5 | Size the pool from concurrency and query time (Little's law), not a guess | HikariCP tuning, PgBouncer |
| 016 | `read-replica-read-write-split` | 5 | Streaming replica for reads, primary for writes and read-your-writes | Aurora readers, routing data source |
| 017 | `partitioning-sharding-deferred` | 5 | Do not shard yet. Know the trigger | Partition or shard when one node cannot cope |
| 018 | `redis-cache-aside` | 6 | Cache menus in Redis with a TTL. Delete on write | Spring Cache, Redis clients |
| 019 | `cache-stampede-single-flight-lock` | 6 | One request refills a hot key. The rest wait | Go `singleflight`, Redisson |
| 020 | `live-tracking-sse-redis-backplane` | 6 | Server-Sent Events for live order status, a Redis channel to reach every instance | Spring SSE, Node SSE |
| 021 | `resilient-external-calls-timeout-bulkhead` | 7 | A timeout and a bulkhead around the payment gateway | Resilience4j, Polly |
| 022 | `modular-monolith-mediatr-payment-module` | 7 | Payment becomes a module with its own context and schema. No reference from Orders | Spring Modulith |
| 023 | `asynchronous-payment-cqrs-lite` | 7 | Take the slow charge off the request path with a channel and a background service | Queue plus worker |
| 024 | `extract-payment-service` | 8 | Payment becomes its own service, for fault and PCI isolation, not for speed | Any service split |
| 025 | `sync-http-inter-service-bridge` | 8 | A temporary HTTP bridge, kept short, to show the coupling that Kafka removes | OpenFeign, RestTemplate |
| 026 | `database-per-service` | 8 | Each service owns its database. Nobody else connects to it | Separate instances |
| 027 | `kafka-async-event-backbone` | 9 | Kafka between services, partitioned by order id, at-least-once | Kafka, Redpanda, Kinesis |
| 028 | `transactional-outbox-inbox` | 9 | Save the event with the state change. A relay publishes it. An inbox makes consumers idempotent | MassTransit, Debezium |
| 029 | `saga-choreography-order-payment` | 9 | Order, payment and delivery coordinate by events, with compensating steps. No 2PC | Temporal or Camunda for orchestration |
| 030 | `authentication-jwt` | 10 | JWT access tokens with roles. Hashed passwords | Spring Security |
| 031 | `authorization-rbac-resource` | 10 | Roles plus ownership, checked in **every** service. The gateway is not a trust boundary | `@PreAuthorize`, policy engines |
| 032 | `pii-data-protection` | 10 | Mask PII in logs. Right to be forgotten means anonymise, keep the money records | Log filters |
| 033 | `extract-delivery-service` | 11 | Delivery is split out for its write-heavy, latest-value profile | Any service split |
| 034 | `polyglot-persistence-location` | 11 | Redis geo for the live rider location, Postgres for assignment history | Redis plus PostGIS |
| 035 | `api-gateway-yarp` | 11 | YARP gateway: path routing and an edge rate limit | Spring Cloud Gateway, Envoy, Kong |
| 036 | `extract-restaurant-service` | 12 | Restaurant is split out because a team owns it (Conway), not for performance | Any service split |
| 037 | `event-carried-state-transfer-read-model` | 12 | Orders keeps a local copy of menu prices, fed by `menu-updated` events | Local read model, materialized view |
| 038 | `zero-downtime-migration-expand-contract-backfill` | 12 | Expand, backfill in small chunks with `SKIP LOCKED`, then contract | Flyway or Liquibase patterns |
| 039 | `cloud-deployment-terraform-ecs-alb` | 12 | The cloud shape (ECS, ALB, Terraform) and a rupee cost model, taught as results not HCL | Any cloud |
| 040 | `observability-opentelemetry-otlp` | 13 | OpenTelemetry to a collector, then Jaeger, Prometheus, Grafana. JSON logs with trace ids | OTel Java agent, Micrometer |
| 041 | `trace-context-propagation-http-kafka-outbox` | 13 | `traceparent` is stored on the outbox row and sent as a Kafka header, so one trace crosses services | OTel propagators |
| 042 | `metrics-cardinality-limits` | 13 | Ids go on spans and logs, never on Prometheus labels | Prometheus guidance |
| 043 | `circuit-breaker-retry-backoff` | 14 | Breaker and jittered retry on **transport** failures only. A decline is final | Resilience4j |
| 044 | `graceful-degradation-dependency-classification` | 14 | Performance dependencies fall back. Correctness dependencies fail honestly | Graceful degradation |

## Part 2: hardening, after Day 14 (ADR-045 to ADR-067)

These are real decisions in the repository. Teach them as "what a production system adds".

| ADR | Topic (file name) | Area | The decision in one line |
| :--- | :--- | :--- | :--- |
| 045 | `restaurant-reject-refund-compensation` | Saga | A restaurant can reject a paid order. The saga cancels it and refunds. `Restaurant:AcceptMode` is `Auto` or `Reject` |
| 047 | `stateless-scale-out-nginx-lb` | Scale-out | Run three identical monolith replicas behind an nginx load balancer |
| 048 | `response-compression-conditional-get` | Performance | Brotli or gzip, plus `ETag` and `If-None-Match` returning 304 on cacheable reads |
| 049 | `distributed-rate-limiting` | Protection | Per-IP rate limit counted in Redis so every replica shares it. Fixed window by default, sliding window by config |
| 050 | `event-envelope-versioning` | Messaging | Event schemas change additively only, with a version field |
| 051 | `dead-letter-queue-poison-messages` | Messaging | After repeated failures a poison message goes to a dead-letter topic and the partition moves on |
| 052 | `field-level-pii-encryption` | Security | Encrypt the phone number at rest with AES-GCM through an EF Core value converter |
| 053 | `payment-tokenization` | Security | The card number becomes a keyed HMAC token immediately. It is never stored |
| 054 | `edge-cache-signed-urls` | Performance | An nginx cache for restaurant reads, and HMAC-signed URLs for invoices |
| 055 | `sse-reconnect-replay-buffer` | Real time | Number every tracking event, keep the last 20 in Redis, honour `Last-Event-ID` on reconnect |
| 056 | `pessimistic-locking-hot-row-coupons` | Concurrency | `SELECT ... FOR UPDATE` for hot coupon rows, chosen after a load test |
| 057 | `keyset-cursor-pagination-order-history` | Performance | Cursor pagination for order history instead of `OFFSET` |
| 058 | `feature-flags-percentage-rollout` | Delivery | Percentage rollout by a stable hash of flag name and user |
| 059 | `backpressure-admission-control` | Protection | A semaphore limits concurrent requests (`Backpressure:MaxConcurrent`) |
| 060 | `load-shedding-priority` | Protection | During an incident, shed low-priority routes first (`LoadShed:Enabled`) |
| 061 | `gateway-weighted-canary` | Delivery | The gateway sends a small share of traffic to a canary Restaurant instance |
| 062 | `restaurant-service-accept-reject` | Saga | The Restaurant service decides accept or reject through Kafka (`Restaurant:DecisionMode`) |
| 063 | `replica-lag-metric` | Observability | `tadka.replica.lag_seconds` shows how stale the local menu replica is |
| 064 | `live-cloud-deployment-azure-container-apps` | Cloud | Live deployment on Azure Container Apps, one resource group per class session, AWS kept as the reference |
| 065 | `login-rate-limiting-account-lockout` | Security | Rate limit login by IP, and lock the account after repeated failures |
| 066 | `refresh-token-rotation-reuse-detection` | Security | Refresh tokens are single-use. Reuse is treated as theft and ends the session |
| 067 | `rs256-jwks-key-rotation` | Security | Sign with RS256 and publish keys at `/.well-known/jwks.json`, so keys can rotate. Replaces the shared HS256 secret |

## How to use this index

- **In a design review,** find the ADR that governs the area and quote its number. If none does, the decision needs a new ADR (068).
- **In an interview,** pick three ADRs that show different kinds of thinking: one that **removes** coupling (037), one about **failure** (043), one about **cost or people** (036).
- **For your own system,** copy the structure, not the decisions. Each ADR here answers: context, options, choice, trade-off, failure mode, revisit when.
