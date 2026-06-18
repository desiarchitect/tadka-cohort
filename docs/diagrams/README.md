# Architecture Diagrams (student-facing)

Mermaid diagrams for every teaching day. Follow [`style-guide.md`](style-guide.md) for colours and shapes.

**In class:** the instructor draws the *before → after* view live on the whiteboard first. These files are the **post-class handout** — polished reference, not a substitute for the live sketch.

| Day | File(s) | ADRs / topics |
|-----|---------|---------------|
| 1 | `day-01-monolith-architecture.md`, `day-01-evolution-timeline.md`, `day-01-final-architecture.md` | 002 monolith-first |
| 2 | `day-02-four-step-framework.md` | 003 schema-per-domain, 008 no-cross-schema FK |
| 3 | `day-03-request-flow.md`, `day-03-er-diagram.md`, `day-03-api-route-map.md` | 005 REST, 010 versioning |
| 4 | `day-04-order-hardening.md` | 011 idempotency, 012 concurrency, 013 domain events |
| 5 | `day-05-scaling.md` | 014–017 indexing, pool, replica, shard deferred |
| 6 | `day-06-cache-patterns.md`, `day-06-sse-backplane.md` | 018–020 cache, stampede, SSE |
| 7 | `day-07-payment-resilience.md` | 021–023 Polly, modular monolith, async payment |
| 8 | `day-08-strangler-fig.md`, `day-08-http-coupling.md` | 024–026 extraction, HTTP bridge |
| 9 | `day-09-outbox-inbox.md`, `day-09-saga-choreography.md` | 027–029 Kafka, Outbox, Saga |
| 10 | `day-10-auth.md` | 030–032 JWT, RBAC, PII |
| 11 | `day-11-geohash.md`, `day-11-rate-limiting.md` | 033–035 Delivery, gateway, rate limits |
| 12 | `day-12-topology.md` | 036–038 Restaurant extraction, read model, backfill |
| 13 | `day-13-observability.md` | 040–042 OTEL, trace propagation, cardinality |
| 14 | `day-14-circuit-breaker.md` | 043–044 breaker, dependency classification |
| 15 | `day-15-swiggy-hld-blueprint.md` | teardown / interview blueprint |
| 16 | `day-16-load-test.md` | load-test types, breaking-point knee |

Instructor-only copies with segment notes also live in `desiarchitect-website/cohort-prep/day-NN/diagram-*.md`.