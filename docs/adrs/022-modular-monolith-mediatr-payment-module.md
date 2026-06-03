# ADR-022: Modular Monolith + MediatR — Carve the Payment Module

**Date:** 2026-06-04
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

The brownout (ADR-021) proves payment must be *isolated*. But isolating the **call** is only half of it — if the Ordering code still `new`s up a `Payment`, writes to the payment table through the shared `TadkaDbContext`, and reads payment state inline, then "Payment" is not a thing we can ever move. Day 8 is supposed to **extract Payment into its own service**, and Week 5 turns these in-process events into Kafka messages. None of that is possible while Payment is welded to Ordering by shared types and a shared `DbContext`.

We also have a home-grown event mechanism from Day 4: `IDomainEventDispatcher` + `IDomainEventHandler<T>` + a reflection-based dispatcher. It works and taught the concept well, but it's bespoke plumbing we now maintain, and it's about to get a third event family (payment). This is the moment to graduate to a standard mediator.

The question is **how much** to refactor. Rewriting every domain (Restaurant, Delivery, Identity) into modules today would be a big-bang with no failure to justify it — exactly the "every decision earned" rule we teach against. We carve **only** the boundary the brownout earned.

## Decision

**Adopt MediatR as the in-process mediator, and carve a single Payment module with its own `DbContext` and zero inbound coupling from Ordering. Modules communicate only through events.**

1. **MediatR replaces the hand-rolled dispatcher.** `IDomainEvent` now extends MediatR's `INotification`; the three existing events (`OrderPlaced`, `OrderConfirmed`, `OrderStatusChanged`) and their handlers (notification, SSE-backplane) become `INotificationHandler<T>`. The controller publishes via `IMediator.Publish(...)` **after** SaveChanges — the ADR-013 "side-effects after commit" rule is unchanged; only the transport is now a maintained library with fan-out, DI registration, and a pipeline for free. We delete `IDomainEventDispatcher`/`DomainEventDispatcher`/`IDomainEventHandler`.
2. **The Payment module owns its data.** A separate `PaymentDbContext` owns the `payment` schema **and its own EF migration history** (`__EFMigrationsHistory` in the `payment` schema). Ordering's `TadkaDbContext` no longer maps `Payment` at all. Today both contexts point at the **same physical Postgres** — this is *logical* separation (own schema, own context, own migrations); the *physical* split (own database) is the Day-8 extraction. That's the honest modular-monolith step: you can already reason about Payment's data in isolation before you pay to move it.
3. **Ordering has zero references to Payment.** No `using Tadka.Api.Domain.Payments` in Ordering. The Payment module reacts to `OrderPlaced` through an `INotificationHandler` it owns; it never reaches back into Ordering's tables. The only contract between them is the **event** — which is precisely what becomes a Kafka topic in Week 5 and an HTTP/event boundary in Day 8.

## Consequences

### Positive
- A **real, testable boundary**: "Ordering doesn't reference Payment" is a property you can grep for and assert. The seam Day 8 extracts is now visible in the code, not aspirational.
- The event contract is transport-agnostic: in-process today → Kafka at Week 5 → the handler shape barely changes.
- Less bespoke plumbing: MediatR is standard, documented, and team-familiar; new engineers recognise it.
- Each module's migrations evolve independently — Payment can add a column without a core migration, mirroring how separate services will own their schemas.

### Negative / Risks
- A new dependency (MediatR) and a **second DbContext** to register, migrate, and reason about (two migration histories now run at startup).
- **Module discipline is now a rule humans must keep** — nothing physically stops someone adding `using ...Payments` in Ordering until Day 8's physical split. We enforce it by review (and could add an architecture test later).
- A cross-module event is **fire-and-forget after commit**: if the Payment handler/processor crashes, the order exists but payment didn't start. ADR-023 owns that failure mode (and Week 5's outbox makes it durable).
- MediatR's licensing changed in v13+; we pin the **last Apache-2.0 release (12.5.0)** to keep the teaching repo OSS and dependency-clean.

### Cost (₹ / effort)
Zero infra (same Postgres, new schema-scoped context). Cost is the migration of three events + two handlers to MediatR, a second `DbContext` + its initial migration, and the discipline of keeping the boundary clean. Bounded, one-time.

## Alternatives Considered
- **Keep the hand-rolled dispatcher, just add payment handlers:** no new dependency, but we keep maintaining bespoke reflection plumbing and miss the standardisation right when complexity grows. The dispatcher served its teaching purpose (Day 4); now it's debt.
- **Refactor *all* domains into modules now:** maximal cleanliness, but a big-bang with no failure to justify it and high regression risk. We carve only the earned boundary; the others stay until *their* failure arrives.
- **Separate physical database for Payment today:** that's the Day-8 extraction. Doing it now pays the operational cost (a second DB, cross-DB consistency) before the boundary has even proven stable. Logical-first, physical-when-extracted.
- **A different mediator (in-house source-gen, Wolverine, etc.):** MediatR is the cohort's lingua franca and the lowest-surprise choice; the pattern, not the library, is the lesson.

## References
- ADR-013 (in-process domain events — the seam this generalises), ADR-008 (no cross-schema FKs — why cross-domain refs are by ID), ADR-021 (resilient gateway call inside this module), ADR-023 (async processing on top of these events)
- Implementation: `Domain/Common/IDomainEvent.cs` (now `: INotification`), `Data/PaymentDbContext.cs`, `Migrations/Payment/*`, `Modules/Payments/*` handlers
- `cohort-prep/day-07/` (modular-monolith teaching package), Day 8 plan (Payment extraction)

## Revisit When
**Day 8:** extract Payment into a standalone service — `PaymentDbContext` gets its own physical database, and the in-process `OrderPlaced` notification becomes a cross-process message (HTTP first, then Kafka in Week 5). **When a second boundary's failure arrives** (e.g. Delivery under load), carve that module the same way. If module discipline keeps slipping, add an automated architecture test that fails the build on a forbidden cross-module `using`.
