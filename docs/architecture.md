# Tadka: Architecture Overview

> A Bangalore food-delivery platform, built failure-first from a .NET 10 monolith
> into **4 services + an API gateway** over 8 weeks. Every box below was *earned*
> by an observed, reproducible failure: never added because it was on a roadmap.
> This document is the portfolio piece: the system, why it looks the way it does,
> and the decision trail behind it.

**Scale canon:** 1 lakh orders/day year-one (~1.2 orders/s avg, ~25/s peak).
**SLOs:** order < 500 ms P99, payment < 300 ms; availability 99.9% overall,
99.99% payments. **Cost:** see [`cost-model.md`](cost-model.md).

---

## 1. The system today (Week 8)

```
                         ┌─────────────────────────────┐
   📱/🌐 clients ──────▶ │  YARP API Gateway  (:8080)  │  one entry · edge rate-limit
                         │  per-service JWT is the floor│  (ADR-035)
                         └──────┬───────┬───────┬───────┘
            ┌───────────────────┘       │       └───────────────────┐
            ▼                           ▼                           ▼
   ┌─────────────────┐        ┌──────────────────┐        ┌──────────────────┐
   │ Monolith        │        │ Payment Service  │        │ Restaurant Svc   │
   │ Ordering+Users  │        │ (:5240)          │        │ (:5260)          │
   │ (:5224)         │        │ own Postgres 5434│        │ own Postgres 5436│
   │ Postgres 5432   │        │ PCI-isolated     │        │ Redis cache      │
   │ + read replica  │        └──────────────────┘        └──────────────────┘
   │   5433          │        ┌──────────────────┐
   │ Redis (cache,   │        │ Delivery Service │   4 services + gateway:
   │  locks, SSE,    │        │ (:5250)          │   Ordering(+Users), Payment,
   │  geo)           │        │ own Postgres 5435│   Delivery, Restaurant
   └─────────────────┘        │ Redis-geo (live) │
                              └──────────────────┘
            └─────────── Apache Kafka (async backbone, SASL/SCRAM) ─────────────┘
              16 topics total: 8 primary + 8 dead-letter queues (*.dlq)
              order-placed · payment-results · order-confirmed · delivery-assigned
              menu-updated · refund-requested · payment-refunded · restaurant-response
              transactional Outbox/Inbox · saga choreography + compensation (ADR-027..029, 045, 051, 062)

   Observability: OpenTelemetry → OTLP Collector → Jaeger (traces) + Prometheus
   (metrics) + Grafana (dashboards). One trace spans the whole saga (ADR-040..042).
```

> The aspirational Day-1 vision is in [`diagrams/day-01-final-architecture.md`](diagrams/day-01-final-architecture.md);
> this is the **as-built** state. The original 5 boxes collapse to the
> **canonical 4 services + gateway**: Ordering and Users stayed together in the
> monolith core; Payment, Delivery, and Restaurant were extracted.

**Inter-service contracts:** async by default over Kafka (durable, decoupled);
synchronous HTTP only where a query genuinely needs a fresh answer. The order hot
path never makes a blocking cross-service call. Pricing reads a **local menu
replica** fed by `menu-updated` events (ADR-037), so orders flow even when
Restaurant is down.

---

## 2. The evolution journey: every move was earned

| Phase | Week | The failure that earned it | The move | ADRs |
|---|---|---|---|---|
| Monolith | 1–2 | Baseline: set NFRs, resist premature optimization | .NET 10 monolith, schema-per-domain, REST `/api/v1` | 001–010 |
| Harden order flow | 4 (Sat) | Double-submit dupes; lost updates; events lost on crash | Idempotency, optimistic concurrency (`xmin` to 409), in-process events | 011–013 |
| Scale database | 3 (Sat) | Seq scans and pool exhaustion under dinner-rush concurrency | Indexes, pool tuning, **streaming read replica + read/write split** | 014–017 |
| Cache + live tracking | 4 (Sun) | Hot menu reads hammer Postgres; no live order view | Redis cache-aside, single-flight stampede lock, SSE over Redis pub/sub | 018–020 |
| Payment brownout | 4 | Slow gateway drains pool: whole monolith hangs (9.2s) | Polly timeout+bulkhead to modular monolith (MediatR) to async payment | 021–023 |
| Extract Payment | 4 | Need fault + PCI + data isolation (not latency) | Own service + own DB; HTTP bridge exposes temporal coupling | 024–026 |
| Kafka backbone | 5 | Sync bridge: Payment down means order lost | Kafka + transactional Outbox/Inbox + saga choreography | 027–029 |
| Secure it | 5 | Unauthenticated endpoints; broad access | JWT auth, RBAC + per-service ownership, PII protection | 030–032 |
| Extract Delivery + gateway | 6 | Distinct scaling profile (geo writes); 3 hosts need one entry | Delivery service (polyglot Redis-geo + Postgres), YARP gateway | 033–035 |
| Extract Restaurant | 6 | Org/Conway boundary; pricing on critical path | 4th service + **event-carried state transfer**, zero-downtime backfill | 036–039 |
| Observability | 7 | Choreographed saga is invisible ("order stuck: where?") | OpenTelemetry, trace-context across HTTP+Kafka+Outbox, cardinality limits | 040–042 |
| Resilience & chaos | 7 | Flaky external dependency cascades failures | Circuit breaker + jittered retry; graceful degradation classes | 043–044 |
| Production & Cloud | 8 | Production readiness, cloud scaling, identity hardening, load reality | Azure Container Apps, load testing, cost modeling, security hardening | 045–067 |

The capstone lesson: **you did not need most of this for 1 lakh orders/day.** You
built it because the business grew into it, and you can now point at the exact
moment each decision became justified. That judgment is the product.

---

## 3. Architecture Decision Records (001–067)

The full reasoning lives in [`docs/adrs/`](adrs/). Each ADR is a real decision:
options, choice, trade-off, failure mode, and revisit criteria. There are 66 ADRs
in total (046 is intentionally skipped in the sequence).

| # | Decision | # | Decision |
|---|---|---|---|
| 001 | .NET 10 | 035 | API gateway (YARP) |
| 002 | Monolith-first | 036 | Extract Restaurant service |
| 003 | Schema-per-domain | 037 | Event-carried state transfer / read model |
| 004 | EF Core code-first | 038 | Zero-downtime migration (expand/contract/backfill) |
| 005 | REST API style | 039 | Cloud deployment (Terraform/ECS/ALB) |
| 006 | RFC 7807 errors | 040 | Observability (OpenTelemetry/OTLP) |
| 007 | Two-layer validation | 041 | Trace-context propagation (HTTP/Kafka/Outbox) |
| 008 | No cross-schema FKs | 042 | Metrics cardinality limits |
| 009 | Denormalize order items | 043 | Circuit breaker + retry/backoff |
| 010 | API versioning | 044 | Graceful degradation / dependency classification |
| 011 | Idempotency for unsafe writes | 045 | Restaurant reject refund compensation |
| 012 | Optimistic concurrency (orders) | 047 | Stateless scale-out NGINX load balancer |
| 013 | In-process domain events | 048 | Response compression and conditional GET |
| 014 | Indexing strategy | 049 | Distributed rate limiting |
| 015 | Connection-pool sizing | 050 | Event envelope versioning |
| 016 | Read replica + read/write split | 051 | Dead letter queue poison messages |
| 017 | Partitioning/sharding deferred | 052 | Field-level PII encryption |
| 018 | Redis cache-aside | 053 | Payment tokenization |
| 019 | Cache-stampede single-flight lock | 054 | Edge cache signed URLs |
| 020 | Live tracking (SSE + Redis backplane) | 055 | SSE reconnect replay buffer |
| 021 | Resilient external calls (timeout+bulkhead) | 056 | Pessimistic locking hot-row coupons |
| 022 | Modular monolith (MediatR, Payment module) | 057 | Keyset cursor pagination order history |
| 023 | Asynchronous payment (CQRS-lite) | 058 | Feature flags percentage rollout |
| 024 | Extract Payment service | 059 | Backpressure admission control |
| 025 | Sync HTTP inter-service bridge | 060 | Load shedding priority |
| 026 | Database-per-service | 061 | Gateway weighted canary |
| 027 | Kafka async event backbone | 062 | Restaurant service accept/reject |
| 028 | Transactional Outbox/Inbox | 063 | Replica lag metric |
| 029 | Saga choreography (order/payment) | 064 | Live cloud deployment (Azure Container Apps) |
| 030 | Authentication (JWT) | 065 | Login rate limiting and account lockout |
| 031 | Authorization (RBAC + resource ownership) | 066 | Refresh token rotation and reuse detection |
| 032 | PII data protection | 067 | RS256 JWKS key rotation |
| 033 | Extract Delivery service | | |
| 034 | Polyglot persistence (location) | | |

---

## 4. Tech stack: and why, not just what

| Layer | Choice | Why this | Why not the alternative |
|---|---|---|---|
| API | .NET 10 (Controllers) | Performance, ecosystem, hiring pool | Node/Go fine too: the **patterns are the lesson**, library is interchangeable (see each `option-space.md`) |
| Database | PostgreSQL 16 | Reliability, JSON, schema-per-domain, `xmin` concurrency | MySQL ok; NoSQL loses relational order/payment invariants |
| Cache | Redis 7 | Cache-aside + locks + pub/sub + GEO in one | Memcached lacks pub/sub + geo |
| Messaging | Apache Kafka | Durable replay, partitioned ordering, at-least-once | RabbitMQ for pure queues; Kafka wins on replay + saga |
| Gateway | YARP (local) / Azure Container Apps + ALB (cloud) | .NET-native, simple routing + rate-limit | Kong/Envoy/APIM at bigger scale (option-space, Day 11) |
| Resilience | Polly | Timeout/bulkhead/retry/circuit-breaker in one pipeline | Hand-rolled is error-prone; Polly is standard |
| Observability | OpenTelemetry + Jaeger/Prometheus/Grafana | Vendor-neutral, free software vs Datadog $/host | Datadog/New Relic = faster setup, recurring per-host bill |
| IaC / deploy | Terraform + Azure Container Apps / ECS Fargate | Results-not-HCL: managed containers, no K8s tax | K8s is overkill at this scale and team size |
| CI/CD | GitHub Actions | Native to the repo | Jenkins = more ops burden |

> **Teaching-repo note:** the code is deliberately over-annotated with `// (ADR-NNN)`
> refs and hand-rolls things production would delegate to libraries
> (MassTransit/NServiceBus, `Microsoft.Extensions.Http.Resilience`, Mapster). The
> wiring is exposed to *teach the mechanics*; the library is named for Monday.

---

## 5. Run it

```bash
docker compose up -d                          # core: 4 Postgres, Redis, Kafka
docker compose --profile observability up -d  # + OTEL/Jaeger/Prometheus/Grafana
# run the 4 services + gateway (see docs/runbooks/) then:
curl http://localhost:8080/api/v1/restaurants # browse through the gateway
```

- **Load test:** [`k6/README.md`](../k6/README.md): smoke to average to stress to spike.
- **Cost:** [`cost-model.md`](cost-model.md): rupees at 1 lakh vs 10 lakh/day.
- **Deploy (cloud):** [`deploy/README.md`](../deploy/README.md): Azure Container Apps / ALB topology.
- **The decision trail:** [`docs/adrs/`](adrs/).
