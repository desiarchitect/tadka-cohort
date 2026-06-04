# ADR-026: Database-per-Service — Payment gets its own Postgres

**Date:** 2026-06-04
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Extracting Payment into its own service (ADR-024) forces the data question. Since Day 7 the Payment module has owned its `PaymentDbContext`, the `payment` schema, and its own migration history — but it has *physically* shared the monolith's Postgres. Two problems remain while the database is shared:

1. **Shared-fate persists at the data layer.** The monolith runs `PaymentDbContext.Migrate()` in its own startup; a broken payment schema or an unavailable payment DB stops the *whole monolith* from booting. A shared database is also a single failure point for both services.
2. **The security boundary is incomplete.** Card-adjacent data sitting in the same database instance as menus and order history is exactly the PCI surface ADR-024 wants to shrink.

A true service owns its data; nothing else reaches into it.

## Decision

**Give the Payment service its own physical PostgreSQL** (`tadka-payment-db`, a second container locally). `PaymentDbContext` points at `ConnectionStrings:PaymentDb`; the Payment service migrates it on its own startup. The monolith no longer references payment data in any way (it hasn't since Day 7 — Ordering has zero payment references), so there is **no cross-service query to lose**: anything Ordering needs about a payment arrives via the `PaymentCompleted`/`PaymentFailed` event contract, never a JOIN. Cross-service data access is **forbidden** — the contract is the only door.

## Consequences

### Positive
- **Independent data failure domain:** kill `tadka-payment-db` and the monolith keeps serving menus and orders; only payment degrades. Reinforces ADR-024's fault isolation.
- **Own backup/retention/scaling/secrets** for payment — and a smaller PCI scope.
- **"Database per service" is now real,** not aspirational — the canonical microservice data rule, demonstrated.

### Negative / Risks
- **Two databases to run, back up, and monitor.** More operational surface.
- **No cross-service JOIN.** "Total orders *with* their payment status for the dashboard" can no longer be one SQL query — it must be composed via the API/events (or a read model later). This is the cost of the boundary, and it's the *correct* cost (it's the coupling ADR-008 forbade all along).

### Cost (₹ / effort)
One more Postgres container locally (one more managed instance in production). Because `PaymentDbContext` already existed and was already scoped to the payment schema (Day 7), pointing it at a new database is a **connection-string change, not a code change**.

## Alternatives Considered
- **Shared Postgres + separate schema (defer the split):** simpler infra today, and a legitimate intermediate step — but it keeps the shared-fate-on-boot and the shared PCI surface, and the own-DB isolation demo wouldn't exist. We take the real step now because payment is money and the change is cheap (a connection string). Physical separation was always the goal; we just stop deferring it.
- **Separate database engine for payment (e.g. an append-only ledger):** over-reach today; revisit if audit/compliance demands a ledger store.

## Cross-stack equivalents
Database-per-service is polyglot-persistence doctrine, language-neutral: a separate `DataSource`/`EntityManagerFactory` in **Spring**, a separate Prisma/TypeORM client in **Node**, a separate `*sql.DB` in **Go** — each pointed at its own instance, with cross-service access only via the API/events. The rule ("a service owns its data; no one else touches it") is identical everywhere.

## References
- ADR-008 (no cross-schema FKs — the same principle, now physical), ADR-022 (PaymentDbContext + own migration history), ADR-024 (the extraction)
- `cohort-prep/day-08/break-kit-day-08.md` (kill `tadka-payment-db` → monolith unaffected)
- Implementation: `src/Tadka.Payment.Api` `PaymentDbContext` on `ConnectionStrings:PaymentDb`; `docker-compose.yml` `tadka-payment-db`

## Revisit When
**Day 11 (ECS deployment):** when services run as separate containers/tasks with independent scaling, the separate database is the natural fit. Reconsider the engine (e.g. a ledger/append-only store) only if payment-audit requirements demand it.
