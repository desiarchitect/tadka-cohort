# Tadka — Architecture Overview

> A Bangalore food-delivery platform, built failure-first from a .NET 10 monolith
> into **4 services + an API gateway** over 8 weeks. Every box below was *earned*
> by an observed, reproducible failure — never added because it was on a roadmap.
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
            └──────────────── Apache Kafka (async backbone) ───────────────┘
              order-placed · payment-results · order-confirmed · delivery-assigned · menu-updated
              refund-requested · payment-refunded · restaurant-response
              transactional Outbox/Inbox · saga choreography + compensation (ADR-027/028/029/045/062)

   Observability: OpenTelemetry → OTLP Collector → Jaeger (traces) + Prometheus
   (metrics) + Grafana (dashboards). One trace spans the whole saga (ADR-040/041).
```

> The aspirational Day-1 vision is in [`diagrams/day-01-final-architecture.md`](diagrams/day-01-final-architecture.md);
> this is the **as-built** state. "5 boxes" on that diagram collapse to the
> **canonical 4 services + gateway** — Ordering and Users stayed together in the
> monolith core; Payment, Delivery, and Restaurant were extracted.

**Inter-service contracts:** async by default over Kafka (durable, decoupled);
synchronous HTTP only where a query genuinely needs a fresh answer. The order hot
path never makes a blocking cross-service call — pricing reads a **local menu
replica** fed by `menu-updated` events (ADR-037), so orders flow even when
Restaurant is down.

---

## 2. The evolution journey — every move was earned

| Phase | Week | The failure that earned it | The move | ADRs |
|-------|------|----------------------------|----------|------|
| Monolith | 1–2 | (baseline) — set NFRs, resist premature optimization | .NET 10 monolith, schema-per-domain, REST `/api/v1` | 001–010 |
| Harden the order flow | 4 (Sat) | double-submit dupes; lost updates; events lost on crash | idempotency, optimistic concurrency (`xmin`→409), in-process events | 011–013 |
| Scale the database | 3 (Sat) | seq scans + pool exhaustion under dinner-rush concurrency | indexes, pool tuning, **streaming read replica + read/write split** | 014–017 |
| Cache + live tracking | 4 (Sun-ish) | hot menu reads hammer Postgres; no live order view | Redis cache-aside, single-flight stampede lock, SSE over Redis pub/sub | 018–020 |
| Payment brownout | 4 | a *slow* gateway drains the pool → the whole monolith hangs (9.2s) | Polly timeout+bulkhead → modular monolith (MediatR) → async payment | 021–023 |
| Extract Payment | 4 | need fault + PCI + data isolation (not latency) | own service + own DB; HTTP bridge → exposed temporal coupling | 024–026 |
| Kafka backbone | 5 | sync bridge: Payment down → order lost | Kafka + transactional Outbox/Inbox + saga choreography | 027–029 |
| Secure it | 5 | the system was wide open | JWT auth, RBAC + per-service ownership, PII protection | 030–032 |
| Extract Delivery + gateway | 6 | distinct scaling profile (geo writes); 3 hosts need an entry | Delivery service (polyglot Redis-geo + Postgres), YARP gateway | 033–035 |
| Extract Restaurant | 6 | org/Conway boundary; pricing on the critical path | 4th service + **event-carried state transfer**, zero-downtime backfill, deploy black-box | 036–039 |
| Observability | 7 | the choreographed saga is invisible ("order stuck — where?") | OpenTelemetry, trace-context across HTTP+Kafka+Outbox, cardinality limits | 040–042 |
| Resilience & chaos | 7 | a flaky external dep needs to fail fast, not cascade | circuit breaker + jittered retry; graceful degradation / dependency classes | 043–044 |
| Interview + portfolio | 8 | turn the build into transferable judgment | masterclass teardowns; **load test, cost model, this document** | — |

The capstone lesson: **you didn't need most of this for 1 lakh orders/day.** You
built it because the business grew into it, and you can now point at the exact
moment each decision became justified. That judgment is the product.

---

## 3. Architecture Decision Records (001–044)

The full reasoning lives in [`docs/adrs/`](adrs/). Each ADR is a real decision:
options → choice → why → trade-off → failure mode → revisit-when (+ cross-stack).

| # | Decision | # | Decision |
|---|----------|---|----------|
| 001 | .NET 10 | 023 | asynchronous payment (CQRS-lite) |
| 002 | monolith-first | 024 | extract Payment service |
| 003 | schema-per-domain | 025 | sync HTTP inter-service bridge |
| 004 | EF Core | 026 | database-per-service |
| 005 | REST API | 027 | Kafka async event backbone |
| 006 | RFC 7807 errors | 028 | transactional Outbox/Inbox |
| 007 | two-layer validation | 029 | saga choreography (order↔payment) |
| 008 | no cross-schema FKs | 030 | authentication (JWT) |
| 009 | denormalize order items | 031 | authorization (RBAC + resource ownership) |
| 010 | API versioning | 032 | PII data protection |
| 011 | idempotency for unsafe writes | 033 | extract Delivery service |
| 012 | optimistic concurrency (orders) | 034 | polyglot persistence (location) |
| 013 | in-process domain events | 035 | API gateway (YARP) |
| 014 | indexing strategy | 036 | extract Restaurant service |
| 015 | connection-pool sizing | 037 | event-carried state transfer / read model |
| 016 | read replica + read/write split | 038 | zero-downtime migration (expand/contract/backfill) |
| 017 | partitioning/sharding deferred | 039 | cloud deployment (Terraform/ECS/ALB) |
| 018 | Redis cache-aside | 040 | observability (OpenTelemetry/OTLP) |
| 019 | cache-stampede single-flight lock | 041 | trace-context propagation (HTTP+Kafka+Outbox) |
| 020 | live tracking (SSE + Redis backplane) | 042 | metrics cardinality limits |
| 021 | resilient external calls (timeout+bulkhead) | 043 | circuit breaker + retry/backoff |
| 022 | modular monolith (MediatR, Payment module) | 044 | graceful degradation / dependency classification |

---

## 4. Tech stack — and why, not just what

| Layer | Choice | Why this | Why not the alternative |
|-------|--------|----------|-------------------------|
| API | .NET 10 (Controllers) | performance, ecosystem, hiring pool | Node/Go fine too — the **patterns are the lesson**, the library is interchangeable (see each `option-space.md`) |
| Database | PostgreSQL 16 | reliability, JSON, schema-per-domain, `xmin` concurrency | MySQL ok; NoSQL loses the relational order/payment invariants |
| Cache | Redis 7 | cache-aside + locks + pub/sub + GEO in one | Memcached lacks pub/sub + geo |
| Messaging | Apache Kafka | durable replay, partitioned ordering, at-least-once | RabbitMQ for pure queues; Kafka wins on replay + the saga |
| Gateway | YARP (local) / ALB (cloud) | .NET-native, simple routing + rate-limit | Kong/Envoy/APIM at bigger scale (option-space, Day 11) |
| Resilience | Polly | timeout/bulkhead/retry/circuit-breaker in one pipeline | hand-rolled is error-prone; Polly is the standard |
| Observability | OpenTelemetry + Jaeger/Prometheus/Grafana | vendor-neutral, ₹0 software vs Datadog $/host | Datadog/New Relic = faster setup, recurring per-host bill |
| IaC / deploy | Terraform + ECS Fargate (black-box) | results-not-HCL; managed containers, no K8s tax | K8s is overkill at this scale and team size |
| CI/CD | GitHub Actions | native to the repo | Jenkins = more ops burden |

> **Teaching-repo note:** the code is deliberately over-annotated with `// (ADR-NNN)`
> refs and hand-rolls things production would delegate to libraries
> (MassTransit/NServiceBus, `Microsoft.Extensions.Http.Resilience`, Mapster). The
> wiring is exposed to *teach the mechanics*; the library is named for Monday.

---

## 5. Run it

```bash
docker compose up -d                          # core: 4 Postgres, Redis, Kafka
docker compose --profile observability up -d  # + OTEL/Jaeger/Prometheus/Grafana
# run the 4 services + gateway (see docs/runbooks/) — then:
curl http://localhost:8080/api/v1/restaurants # browse through the gateway
```

- **Load test:** [`k6/README.md`](../k6/README.md) — smoke → average → stress → spike.
- **Cost:** [`cost-model.md`](cost-model.md) — ₹ at 1 lakh vs 10 lakh/day.
- **Deploy (black-box):** [`deploy/README.md`](../deploy/README.md) — ALB/ECS topology, results not HCL.
- **The decision trail:** [`docs/adrs/`](adrs/).
