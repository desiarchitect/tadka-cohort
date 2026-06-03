# ADR-020: Live Order Tracking — Server-Sent Events + Redis Pub/Sub Backplane

**Date:** 2026-06-02
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Day 4 designed the live-tracking contract (`websocket-contract.md`) but deliberately built nothing, because a correct implementation needs a **backplane** — and Redis didn't exist yet. Now it does. The customer staring at "where's my biryani?" should see status updates (and later, the rider's location) **pushed** to them, not have the app poll every 2 seconds (thousands of wasteful "nothing changed" requests/sec against the DB at dinner-rush concurrency).

The hard part is not the wire protocol. It's that a streaming connection lives on **one** app instance. The moment Tadka runs two instances (which it will, for availability — Week 6 ECS), a status update processed on instance B cannot reach a customer whose stream is held by instance A. Without a shared channel, updates are silently lost.

## Decision

**Server-Sent Events (SSE) for the stream, Redis Pub/Sub as the backplane.**

- **Transport: SSE** — `GET /api/v1/orders/{id}/events` returning `text/event-stream`. Live tracking is **one-way** (server → client), and SSE is the simplest fit: plain HTTP, auto-reconnect built into browsers, no upgrade handshake. WebSocket is reserved for a genuine two-way need (e.g. in-app chat with the rider); SignalR's abstraction isn't worth its weight for one-way push.
- **Backplane: Redis Pub/Sub.** An order status change **publishes** an event to channel `order:{id}`. This is wired off the existing **Day-4 in-process domain events** (ADR-013): a new handler publishes `OrderStatusChanged` to Redis after the transition is committed. The SSE endpoint **subscribes** to `order:{id}` and writes each message to the open stream until the client disconnects.
- **Why this composes:** any instance can publish; the instance holding the connection is subscribed and receives it. The in-process event seam from Day 4 becomes a Redis publish today, and becomes a **Kafka** publish at extraction (Week 5) — the domain code barely changes.

## Consequences

### Positive
- Push instead of poll: no wasteful 2s polling, far less DB load, lower latency to the customer.
- Multi-instance-correct from day one via the backplane — no "works on one box, breaks on two" surprise at ECS (Week 6).
- Reuses the Day-4 domain-event seam; the publisher doesn't know who's listening (fan-out), exactly like the eventual Kafka consumers.

### Negative / Risks
- **Stateful connections:** each open SSE stream consumes server memory and, behind a load balancer, needs sticky sessions (or any instance works *because* of the backplane — that's the point). Reconnect/heartbeat handling required.
- **At-least-once-ish pub/sub:** Redis pub/sub is fire-and-forget — a publish during a subscriber blip is lost. Acceptable for *tracking* (the next update corrects it; SSE clients also re-fetch on reconnect). **Not** acceptable for orders/payments, which stay on durable paths.
- More moving parts (a subscription per connection). Bounded by concurrent viewers, not total orders.

### Cost (₹ / effort)
Reuses the Redis already added for caching (ADR-018) and the domain-event dispatcher already built (ADR-013). The new code is an SSE action + a publish handler + a subscribe loop. No new infra.

## Alternatives Considered
- **HTTP polling every N seconds:** dead simple, but thousands of "nothing changed" requests/sec hammer the DB, and the update is still late. Rejected — it's the very waste we're removing.
- **WebSocket / SignalR:** full-duplex and feature-rich, but tracking is one-way; the connection-lifecycle weight and (for SignalR) the .NET-specific abstraction aren't justified yet. Revisit if a two-way feature appears.
- **In-memory hub, no backplane:** works on a single instance, silently drops updates the moment there are two. Rejected — it builds in the exact bug we're here to avoid.
- **Kafka as the backplane now:** Kafka is Week 5, earned by cross-service decoupling/durability needs. For in-cluster fan-out of ephemeral tracking events, Redis pub/sub is lighter and already present. Revisit at extraction.

## References
- ADR-013 (in-process domain events — the publish seam), ADR-018 (Redis client), Day-4 `cohort-prep/day-04/websocket-contract.md` (the contract this implements)
- `cohort-prep/day-03/api-style-selection.md` (SSE vs WebSocket vs Kafka trade-offs)
- Implementation: `Controllers/OrderTrackingController.cs` (SSE), `Infrastructure/Realtime/*` (Redis publish/subscribe), order status-change event handler

## Revisit When
When **delivery-agent GPS** arrives (Week 5, Delivery service) the publisher becomes that service emitting location to `order:{id}` — likely over **Kafka**, with Redis pub/sub (or a managed realtime service) fanning out to connections. When running **many instances at scale** (Week 6+), reconsider connection management (a dedicated realtime tier / managed service) and whether SSE needs upgrading to WebSocket for new two-way features.
