# ADR-036: Wire Delivery Location Updates onto the Live-Tracking Backplane

**Date:** 2026-06-06
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

ADR-020 built SSE + Redis pub/sub for live order tracking and named delivery-agent GPS as the next
publisher under "Revisit When." ADR-033/034 then extracted Delivery with its own `PUT
/deliveries/{orderId}/location`, but that endpoint only ever wrote to Redis-geo (`GEOADD`, ADR-034) —
it never published onto the `order:{id}` channel `RedisOrderTrackingBus` and the customer's `GET
/orders/{id}/events` SSE stream already use for status changes. Both halves existed; they weren't
connected. A customer watching their order never saw "rider is 200m away" — only status flips.

## Decision

**`PUT /deliveries/{orderId}/location` now also publishes onto the same `order:{id}` Redis channel**,
alongside the existing `GEOADD`, whenever the agent is actively on that delivery (`Assigned` or
`PickedUp` — not `Delivered`/`Cancelled`, nobody's watching those).

- **One event shape, not two:** `OrderTrackingEvent` (both the monolith's and Delivery.Api's copy of it)
  gained optional `Latitude`/`Longitude`. A status-change event leaves them null; a location ping sets
  `Status = "RiderLocation"` and populates them. The SSE client reads one JSON shape either way — no
  special-casing which kind of event arrived.
- **No new transport, no shared project reference.** Delivery.Api reuses the `IConnectionMultiplexer`
  singleton already registered for `ILocationStore` (ADR-034) to `PUBLISH` onto `order:{orderId}`.
  Delivery.Api does **not** reference `Tadka.Api` (ADR-033 keeps them separate deployables) — it defines
  its own `OrderTrackingPublisher.cs` with a wire-compatible mirror of `OrderTrackingEvent`, kept in sync
  by hand across the boundary exactly like `OrderConfirmedMessage`/`DeliveryAssignedMessage` already are
  in `Messaging/Messaging.cs`.
- **`IOrderTrackingPublisher` / `NullOrderTrackingPublisher`** mirror the `ILocationStore` /
  `NullLocationStore` pattern: no Redis configured ⇒ no-op, so the test suite stays Redis-free.

## Consequences

### Positive
- Closes the exact gap ADR-020 flagged as "Revisit When" — the customer's SSE stream now carries rider
  location, not just status.
- Zero new infrastructure: same Redis connection, same channel, same subscribe loop on the monolith side.
- The publish is conditioned on assignment status, so a location ping after delivery/cancellation doesn't
  spam a stream nobody's reading.

### Negative / Risks
- Two hand-maintained copies of `OrderTrackingEvent` (monolith + Delivery.Api) that must stay
  wire-compatible — the same risk `Messaging.cs`'s Kafka contracts already carry, not a new category of
  risk.
- Still Redis pub/sub (fire-and-forget, per ADR-020) — a location ping during a subscriber blip is lost.
  Acceptable for the same reason status pings are: the next ping corrects it.

### Cost (₹ / effort)
No new infra. One new small file in Delivery.Api, one optional-field addition to the existing event
record, ~10 lines in the location endpoint.

## Alternatives Considered
- **A parallel location-only channel/endpoint:** rejected — doubles the client's subscription surface for
  no benefit; the existing `order:{id}` channel already fans out fine to one subscriber.
- **Delivery.Api referencing `Tadka.Api` directly for `OrderTrackingEvent`:** rejected — breaks the
  service-extraction boundary ADR-033 just drew; the wire-contract-duplication convention already exists
  in this repo for exactly this situation.

## References
- ADR-020 (SSE + Redis pub/sub backplane — this is its "Revisit When" item), ADR-033 (Delivery
  extraction), ADR-034 (Redis-geo location store)
- Implementation: `src/Tadka.Delivery.Api/OrderTrackingPublisher.cs`, `Program.cs` (location endpoint);
  `src/Tadka.Api/Infrastructure/Realtime/OrderTrackingEvent.cs`
- Tests: `tests/Tadka.Delivery.Api.Tests/LocationTrackingTests.cs`
