# ADR-036: Extract the Restaurant Service (the 4th service — an org/ownership trigger, and the hardest extraction)

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Restaurant (the catalog: restaurants + menus) is the **last** domain still living in the monolith. We deliberately left it for last, and on Day 11 we said so out loud: Restaurant is **read-heavy and already cached** (Day-6 Redis cache-aside) — there is **no performance failure** demanding extraction. So why extract it at all?

Two honest reasons, neither of which is a perf brownout:
1. **Org/ownership + deployment cadence (Conway's Law).** A separate catalog/menu team should own the menu domain and ship it on its own cadence without coordinating a monolith deploy. The system boundary should mirror the team boundary.
2. **Completing the canonical boundary set** — "4 services + gateway" is the target end-state; Restaurant is the 4th.

This is the **fourth distinct extraction trigger**, and naming it matters pedagogically: Payment = fault/PCI isolation (ADR-024), Delivery = scaling/durability profile (ADR-033), **Restaurant = organizational/Conway**. The lesson: *not every extraction is earned by a technical failure — sometimes the driver is team topology.* The anti-pattern is extracting for a résumé, not a reason.

But Restaurant is also the **hardest** extraction, for a reason that has nothing to do with the trigger: it is read **synchronously on the critical path of order creation**. `OrdersController` loads the restaurant + menu in-process to **price every order server-side** (the client never sends a price — a security invariant). Naively moving that read behind an HTTP call re-introduces the exact temporal coupling we removed on Day 8 (a down peer blocks the caller) — except now on the **order hot path**, the worst possible place. That coupling problem is solved separately in **ADR-037** (a local read model fed by events); this ADR covers the extraction itself.

## Decision

**Extract `Tadka.Restaurant.Api`** as the 4th service — a **move, not a rewrite** (same pattern as Payment Day 8 / Delivery Day 11): its own process, own database (`restaurant-db`, port 5436), own migration history, per-service JWT (ADR-031). The Restaurant + MenuItem domain, the `RestaurantsController` (CRUD + menu), its EF configuration, contracts, and validators move out of the monolith. The **Day-6 Redis cache-aside** moves *with* it (Restaurant is the read-heavy/cached service — the cache belongs to its owner). The monolith **drops** the `restaurant` schema/domain and no longer references Restaurant types (enforced by `BoundaryTests`).

The cross-service read on the order path is **not** solved with a synchronous call. Restaurant **publishes `menu-updated`/`restaurant-updated`** via its Outbox (ADR-028); the monolith keeps a local price read model (ADR-037). The gateway (ADR-035) gains a `/api/v1/restaurants/**` route to the new service.

## Consequences

### Positive
- The menu/catalog team owns its service, schema, and deploy cadence — boundary mirrors ownership (Conway).
- Canonical **4 services + gateway** reached; each service owns its data.
- The read-heavy cache lives with its owner; menu reads no longer touch the monolith.

### Negative / Risks
- A **5th datastore** (restaurant-db) + more operational surface — and we accept this for an org reason, not a perf one (be honest about the cost).
- The order path now depends on Restaurant data it no longer owns → **must** solve the critical-path read (ADR-037) or availability regresses.
- A data migration: Restaurant's rows must move to the new DB and the monolith's read model must be seeded — **online, no downtime** (ADR-038).

### Cost (₹ / effort)
A new service + DB; **low** because Kafka/Outbox/Inbox, per-service JWT, and the Redis cache already exist (all reused). The real cost is the read-model + migration work (ADR-037/038), not the service shell.

## Alternatives Considered

### Option A: Keep Restaurant in the monolith
- Pros: zero migration; the in-process priced read "just works"; no 5th DB.
- Cons: the menu team can't own its cadence; the canonical boundary stays incomplete.
- Why rejected: the org/ownership trigger is real; and the cohort's end-state is 4 services + gateway. (Honest caveat: in a *real* company with no separate menu team, **not extracting** would be the right call — we say this explicitly in teaching.)

### Option B: Extract, and read the menu over synchronous HTTP at order time
- Pros: simplest extraction; no read model to build.
- Cons: re-introduces Day-8 temporal coupling **on the order hot path** — Restaurant down → no orders.
- Why rejected: unacceptable availability regression on the most important path → ADR-037 instead.

## Teaching fields

- **Topic:** When (and why) to extract a service that has **no performance problem** — the organizational/Conway trigger — and why a read-on-the-critical-path is the hardest kind to extract.
- **Options:** keep in monolith · extract + sync read · **extract + local read model (chosen, see ADR-037)**.
- **Choice:** extract Restaurant as the 4th service (move-not-rewrite, own DB, per-service JWT, cache moves with it); solve the order-path read via ADR-037.
- **Why:** boundary should mirror team ownership; completes the canonical set; the cache belongs with its owner.
- **Trade-off:** a 5th datastore and a migration, accepted for an *org* reason — and an honest "you might not do this without a separate team."
- **Failure mode** (2 AM dinner rush): if you extract and read the menu synchronously, Restaurant hiccups → **every** order fails to price → order intake stops. (Fixed by ADR-037 — orders price from the local replica and keep flowing.)
- **Revisit when:** there is no separate menu team and no other driver → *don't* extract (re-merge is valid). Revisit the Saga to orchestration only if Restaurant ever becomes a transaction participant (today it is a read dependency, not a saga step).
- **Cross-stack equivalents:** a new service is a new Spring Boot app / NestJS app / Go service anywhere; the Conway trigger ("Team Topologies", Inverse Conway Maneuver) is language-neutral. Move-not-rewrite + own DB is the universal extraction discipline (ADR-026).

## References
- ADR-024 (Payment — fault/PCI trigger), ADR-033 (Delivery — scaling trigger), ADR-037 (local read model — solves the order-path read), ADR-038 (zero-downtime data migration), ADR-026 (database-per-service), ADR-035 (gateway route), ADR-009 (denormalized order items)
- `cohort-prep/day-12/option-space.md` (extraction-trigger matrix), `saga-deep-dive.md`
- Implementation: `src/Tadka.Restaurant.Api/*`; monolith drops `Domain/Restaurants`

## Revisit When
The canonical 4 services + gateway is now complete; further extraction (e.g. splitting Identity) needs its **own** earned trigger. If the org consolidates teams, re-merging Restaurant into the monolith is a legitimate reversal — the boundary should keep mirroring ownership.
