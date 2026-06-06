# ADR-037: Event-Carried State Transfer — a Local Read Model for a Cross-Service Read on the Critical Path

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Extracting Restaurant (ADR-036) breaks one specific thing: order creation prices every order **server-side** by reading the restaurant's menu in-process (`_db.Restaurants.Include(r => r.Menu)`). The client never sends a price — pricing is a **security invariant** (trusting a client price = a discount exploit). Once the menu lives in another service with its own database (ADR-026), the monolith can no longer do that in-process read.

The obvious fix — call the Restaurant service over HTTP at order time — is **wrong here**. We spent Day 8 learning that synchronous inter-service calls couple caller and callee **in time** (a down peer blocks the caller). Putting that coupling on the **order hot path** is the worst place for it: Restaurant has a slow GC pause or a deploy blip → *every* order stalls or fails. We do not accept a temporal-coupling regression on the most important path of the business, for a service we extracted for *organizational* reasons.

The order itself is already designed to **denormalize** the price at capture time (ADR-009: `OrderItem` stores name + unit price, so an order is immune to later menu changes). The only thing that needs live menu data is the **validation + pricing at the moment of order creation**. That is a **read**, and reads of another service's data are a solved pattern.

## Decision

The monolith keeps a **local read model** of the menu — a denormalized, read-only **price replica** in its *own* `ordering` schema (`ordering.menu_replica`: restaurantId, menuItemId, name, price, isAvailable, restaurant address, updatedAt). Order creation **prices from the replica**, never from a cross-service call. The replica is Ordering's own table — **no cross-schema FK** (ADR-008); it is a cache of data Restaurant owns, not a shared table.

The replica is fed by **event-carried state transfer**: Restaurant publishes **`menu-updated`/`restaurant-updated`** (via its Outbox, ADR-028) whenever a price/availability/address changes; the monolith **consumes** these and **upserts** the replica (Inbox dedup, ADR-028 — reused from Day 9). The event **carries the state** (the new menu rows + address) so the monolith never calls back (ADR-008 discipline).

To *teach the contrast*, a config lever `Ordering:RestaurantReadMode = SyncHttp | LocalReplica` keeps both code paths: `SyncHttp` (the naive read, reusing the Day-7/8 Polly pipeline) demonstrates the temporal-coupling wound; `LocalReplica` (default) is the decision.

## Consequences

### Positive
- **Availability over the order path:** Restaurant down → orders still price from the replica and flow (the payoff demo). The order path depends only on Ordering's own datastore.
- No back-call (event carries the data) — clean boundary (ADR-008).
- Reuses the Day-9 Kafka/Outbox/Inbox backbone — little new machinery.

### Negative / Risks
- **Eventual consistency:** a **stale-price window** between a price change and the replica catching up. A price raised at 19:00:00 may sell at the old price for a few seconds.
- Data duplication: the menu now lives in two places (Restaurant's DB + Ordering's replica) — they can drift if events are lost (mitigated by the Outbox's at-least-once + a periodic reconciliation sweep).
- Another consumer + table to operate.

### Cost (₹ / effort)
Low marginal cost — the consumer/Inbox/Outbox patterns exist. The honest business cost is the *stale-price window*, which is acceptable because order line prices are snapshotted at capture (ADR-009) and the window is seconds.

## Alternatives Considered

### Option A: Synchronous HTTP read at order time (+ circuit breaker)
- Pros: always fresh price; no replica; trivial.
- Cons: temporal coupling on the hot path; Restaurant latency/availability becomes the order path's latency/availability; circuit-breaker-open → still can't price.
- Why rejected: an availability regression on the most critical path, for a non-perf extraction. (Kept behind `SyncHttp` as the teaching "wrong way".)

### Option B: Shared Redis menu cache both services read
- Pros: fast; single copy.
- Cons: two services share one cache = **shared data ownership** (who invalidates? who owns the schema?) — erodes the boundary we just drew.
- Why rejected: weakens service ownership; the replica is Ordering's *own* projection instead.

### Option C: API composition / pricing at the gateway
- Pros: centralizes the join.
- Cons: pricing is core domain logic + a security invariant — the wrong layer for a thin gateway (ADR-035); puts the coupling at the edge.
- Why rejected: domain logic does not belong in the gateway.

### Option D: Trust a client-supplied price
- Why rejected: a security hole (discount exploit). Server-side pricing is non-negotiable.

## Teaching fields

- **Topic:** How a service reads another service's data **without** coupling to it in time — event-carried state transfer / a local read model (the CQRS "read side" of a boundary), and the consistency trade it buys.
- **Options:** sync HTTP (+breaker) · shared cache · **local read model fed by events (chosen)** · API composition · trust client (insecure).
- **Choice:** denormalized `ordering.menu_replica` upserted from `menu-updated` events (Inbox dedup); price from the replica; `SyncHttp` lever kept only to demonstrate the wound.
- **Why:** availability of the order path > price freshness; events carry state so no back-call; reuses the existing backbone.
- **Trade-off:** eventual consistency — a seconds-long stale-price window; data duplication that can drift without the Outbox + reconciliation.
- **Failure mode** (2 AM dinner rush): `menu-updated` events lag (Kafka backpressure) → the replica is stale → a few orders sell at the old price. Bounded by Outbox at-least-once + a reconciliation sweep; a mispriced order is a business decision (honor / reprice), not a crash. Compare: with `SyncHttp`, the same Restaurant blip stops **all** orders.
- **Revisit when:** pricing must be **transactionally** exact at order time (e.g. surge pricing that must never sell stale) → accept a synchronous read with a tight circuit breaker + fallback, or move pricing into the catalog service behind a fast read. Add a scheduled reconciliation if drift is observed.
- **Cross-stack equivalents:** event-carried state transfer is the same everywhere — a Kafka/Rabbit consumer maintaining a local projection: spring-kafka `@KafkaListener` → JPA upsert · kafkajs/NestJS handler → Prisma upsert · kafka-go consumer → sqlc upsert. The pattern = "own a read-only replica of upstream data, fed by its events" (Fowler's *EventCarriedStateTransfer*; CQRS read model).

## References
- ADR-036 (Restaurant extraction — why this is needed), ADR-009 (order items denormalize price at capture), ADR-008 (no cross-schema FK; replica is Ordering-owned), ADR-027/028 (Kafka + Outbox/Inbox reused), ADR-025 (sync HTTP + Polly — used only by the `SyncHttp` lever), ADR-038 (seeding the replica via online backfill)
- `cohort-prep/day-12/option-space.md` (cross-service read patterns), `break-kit-day-12.md`
- Implementation: monolith `ordering.menu_replica` + `MenuUpdatedConsumer`; `Ordering:RestaurantReadMode`

## Revisit When
If the stale-price window ever causes real revenue/CX issues, move to a synchronous priced read **inside** the catalog service (Ordering sends items, Restaurant returns the priced order) behind a circuit breaker — trading availability back for freshness, consciously.
