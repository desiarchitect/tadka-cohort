# ADR-033: Extract the Delivery Service (the 3rd service — a different scaling profile)

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

The `Delivery` domain (agents, assignments, live location) has existed since Day 2 but nothing uses it — orders never get a rider. Now we build real-time delivery, and its **workload is fundamentally different** from order creation: rider location updates arrive **every few seconds per active agent** (high-frequency), only the **latest** position matters (low durability), and tracking reads must be **sub-millisecond**. Mixing that into the monolith's Postgres + shared connection pool would have location writes contending with order creation — different access pattern, different scaling and durability profile.

This is a **third, distinct extraction trigger**: not fault/PCI isolation (Payment, ADR-024) and not org/deploy coupling (Week 6 Conway) — but a **scaling/durability-profile mismatch**.

## Decision

**Build delivery, then extract `Tadka.Delivery.Api`** (a move, not a rewrite — the same pattern as Payment, Day 8): its own process, own database (`delivery-db`), own migration history, per-service JWT (ADR-031), and Kafka-driven over the Day-9 backbone. The monolith publishes **`order-confirmed`** (via the Outbox, carrying the orderId + delivery lat/long) when an order auto-confirms after payment; Delivery **consumes** it, assigns an available agent, and **publishes `delivery-assigned`**. Live location lives in **Redis-geo** (ADR-034); the durable assignment/history lives in the Delivery service's Postgres. The monolith **drops** the delivery schema/domain (it owns it no longer).

## Consequences

### Positive
- Location's high-frequency writes hit **Redis**, never the order-path Postgres pool — no contention with order creation.
- A 3rd independent failure domain: kill Delivery → ordering, payment, browsing are all unaffected (only live tracking degrades).
- Delivery can scale on its own (location ingestion) independent of order intake.

### Negative / Risks
- A 4th datastore to run (delivery Postgres) + Redis-geo. More operational surface.
- The order→payment→**delivery** flow is now a **3-participant Saga** — choreography still works but the implicit flow is growing (ADR-029 revisit: orchestration becomes worth considering; see `saga-deep-dive.md`).
- Eventual consistency extends another hop (order Confirmed → rider assigned moments later).

### Cost (₹ / effort)
A new service + DB + Redis usage; low *because* the Kafka backbone, Inbox/idempotency, and per-service JWT already exist (reused). The saving: location load never threatens the order path.

## Alternatives Considered
- **Keep Delivery in the monolith:** location writes pressure the order path's pool; rejected once tracking is real-time.
- **Extract but keep location in Postgres/PostGIS:** see ADR-034 — table/index bloat at 200 writes/s; rejected for live location (kept for history).
- **Extract everything at once (Delivery + Restaurant now):** Restaurant is read-heavy + already cached (no bottleneck) — no earned trigger today; deferred to Day 12.

## Cross-stack equivalents
A new service is a new Spring Boot app / Nest app / Go service everywhere; the *trigger* (a workload with a different scaling/durability profile → its own service + the right datastore) is the language-neutral lesson. Kafka-driven assignment ≈ spring-kafka listener / kafkajs / kafka-go consumer.

## References
- ADR-024 (Payment extraction — a *different* trigger), ADR-027/028 (Kafka + Outbox/Inbox reused), ADR-029 (Saga — now 3 participants), ADR-034 (Redis-geo), ADR-035 (gateway)
- `cohort-prep/day-11/break-kit-day-11.md`, `saga-deep-dive.md`
- Implementation: `src/Tadka.Delivery.Api/*`; monolith `order-confirmed` outbox publish

## Revisit When
Restaurant extraction (Day 12) completes the canonical **4 services + gateway**. Revisit the Saga to **orchestration** if a 4th participant or complex branching appears (`saga-deep-dive.md`). Add location **history** export (Redis → a time-series/analytics store) if "average delivery time by zone" analytics are needed.
