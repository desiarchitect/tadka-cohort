# ADR-024: Extract Payment into a Separate Service (the first physical service boundary)

**Date:** 2026-06-04
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Day 7 fixed payment's **latency** coupling: a Polly timeout + bulkhead (ADR-021) stopped a slow gateway from draining shared resources, and async processing off the request path (ADR-023) means `POST /orders` no longer waits for the bank. The Payment module is also already a clean, grep-provable boundary (ADR-022): its own `DbContext`, schema, migration history, and events — Ordering has **zero** references to it.

But Payment still shares **one process and one database** with Ordering. That leaves failure modes that no in-process pattern can fix:

1. **Shared fate at the host level.** A fatal in the payment path that *isn't* a caught poison-item — an OOM, a dependency that calls `FailFast`, or a payment-schema migration that fails on boot — takes the **whole monolith host** down with it (menu, order intake, everything). The Day-7 bulkhead bounds *concurrency*; it does not bound *process death*. (Today the monolith even runs `PaymentDbContext.Migrate()` in its own startup — a broken payment DB stops the *entire* app from booting.)
2. **Security / PCI surface.** Card data and gateway secrets live in the same process and database as menus and order history. A money service deserves its own blast radius and its own secrets.
3. **Coupled scaling & deploy** (named; the org/deploy angle is fully developed in Week 6 for Delivery/Restaurant). Payment is gateway/IO-bound and bursty; it scales differently from order intake.

Payment is **money**, it is the **cleanest seam we have**, and these are reasons to give it its **own failure domain and its own data** — not a calendar. This is the first time a Tadka service becomes *physical*.

## Decision

**Extract the Payment module into a standalone service, `Tadka.Payment.Api`** — its own process, its own database (ADR-026), reached over HTTP (ADR-025). Because Day 7 already isolated the seam, this is a **move, not a rewrite**: the gateway, `PaymentService`, `PaymentDbContext`, resilience pipeline, options, and the `Payment` entity move to the new project essentially unchanged. The monolith keeps its async orchestration (the `OrderPlaced` handler → bounded queue → background processor) but the processor now calls the Payment service via a typed, Polly-wrapped `IPaymentClient` instead of an in-process `PaymentService`. Ordering still communicates only through the shared `PaymentCompletedEvent`/`PaymentFailedEvent` contract.

Scope is **Payment only**. Delivery and Restaurant stay in the monolith until *their* failure earns extraction (Week 6).

## Consequences

### Positive
- **Independent failure domain:** kill the Payment service and the monolith keeps serving menus and accepting orders (they queue/pend). A payment fatal can no longer take the platform down.
- **Independent data + security domain:** payment data and gateway secrets live in their own service and database (ADR-026) — a smaller PCI surface.
- **Independent deploy/scale:** a payment change ships without redeploying the monolith; payment can scale on its own.
- The grep-clean Day-7 boundary **pays off**: extraction is a transport swap, not a redesign.

### Negative / Risks
- A **network hop** replaces an in-process call: partial failure, retries, distributed debugging, eventual consistency. The synchronous-HTTP version of this (ADR-025) means *Payment service down ⇒ the charge can be lost from the in-memory queue* — the exact gap that earns Kafka + Outbox on Day 9.
- **Two services + two databases to run, deploy, and observe.** Operational cost goes up; this is only worth it because payment is money and the seam was already clean.

### Cost (₹ / effort)
One new small service + one more Postgres container locally. The engineering cost is low *because of* Day 7 (clean seam); it would have been a multi-week untangling on a Big-Ball-of-Mud monolith. The saving: payment incidents stop being platform incidents.

## Alternatives Considered
- **Stay a modular monolith (status quo):** cheapest to run, but keeps the shared-host failure + shared PCI surface. Rejected once payment is real money.
- **Extract everything now:** premature — Delivery/Restaurant haven't shown a failure that earns the operational cost (the very anti-pattern the cohort warns against). Rejected.
- **Latency as the trigger (the old Day-8 premise):** already solved on Day 7 — re-using it would teach a false narrative. Rejected; the honest trigger is fault/security/data isolation.

## Cross-stack equivalents
Service extraction is architecture, not .NET: the same move is a new **Spring Boot** app split from a modular monolith (separate Gradle/Maven module → deployable), a new **NestJS** app in a monorepo, or a new **Go** service. The discipline — extract only a *clean, failure-justified* seam, keep communication on a contract — is identical in every stack.

## References
- ADR-021 (timeout/bulkhead — fixed *latency*, not process fate), ADR-022 (the clean seam this extracts), ADR-023 (async — preserved), ADR-025 (HTTP bridge), ADR-026 (own database)
- `cohort-prep/day-08/break-kit-day-08.md` (crash/fault + own-DB isolation demos)
- Implementation: `src/Tadka.Payment.Api/*`, monolith `Modules/Payments/IPaymentClient.cs`

## Revisit When
**Week 6:** extract Delivery then Restaurant (canonical order Payment → Delivery → Restaurant) once the *organizational* deploy-coupling failure appears → end state **4 services + gateway**. Re-evaluate the boundary if payment and ordering turn out to be far chattier than the event contract assumes.
