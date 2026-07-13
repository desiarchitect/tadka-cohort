# ADR-055: SSE Reconnect Replay — a Bounded Buffer, Not Durability

**Date:** 2026-07-12
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

ADR-020's live tracking pushes status changes over Redis pub/sub to connected SSE clients. Pub/sub
is fire-and-forget: a client that is disconnected (network blip, phone locked, laptop closed lid)
when an event publishes never sees it, and a naive reconnect just starts a NEW stream from
"whatever the current status is now" — silently skipping every intermediate state
(`Confirmed`/`Preparing`/`ReadyForPickup` might all fire while the client is offline). For a
status timeline UI, that's a visibly broken experience: the customer's screen jumps straight to
"PickedUp" with no explanation.

## Decision

**Give every tracking event a per-order monotonic sequence number, keep the last 20 events in a
capped Redis LIST (6-hour TTL), and honor the standard SSE `Last-Event-ID` reconnect header to
replay what was missed before resuming the live stream.**

- `RedisOrderTrackingBus.PublishAsync` increments a per-order sequence counter and appends
  `{seq, event}` to a capped list (`LTRIM` to the last 20) BEFORE publishing to the pub/sub
  channel.
- Every SSE frame carries `id: {seq}`.
- On reconnect with `Last-Event-ID`, the endpoint replays every buffered event with a higher seq,
  THEN resumes live streaming — de-duplicating against the replay using the same seq.

## Consequences

### Positive
- A dropped connection during a status change no longer loses that status — the client catches up
  automatically on reconnect.
- The mechanism utilizes the standard SSE reconnect contract (`Last-Event-ID`), ensuring compatibility with native client behaviors.

### Negative
- **This is a buffer, not durability.** 20 events / 6 hours are hard limits: a client offline
  longer than that, or an order with more than 20 transitions in its history, loses events beyond
  the window. The buffer's job is
  covering realistic reconnect gaps, not arbitrary outages.
- Every publish now costs 3 Redis operations (INCR, RPUSH+LTRIM, PUBLISH) instead of 1.
- No cross-instance ordering guarantee beyond what Redis itself provides.

### Risks
- A silent gap (client offline longer than the buffer holds) still fails invisibly. **Mitigation:** The UI already re-fetches current status on connect regardless, so the end state is always eventually correct.

## Alternatives Considered

### Option A: No replay — always start fresh on reconnect (status quo before this ADR)
- Pros: Simplest implementation.
- Cons: A reconnect during a transition silently skips states, causing invisible data loss in a customer-facing UI.

### Option B: Durable delivery via the transactional outbox
- Pros: Would eliminate the buffer's window entirely (true no-loss delivery).
- Cons: High overhead for a transient UI state update. Guaranteed at-least-once delivery across service boundaries is overkill for live status feeds.
- Why rejected: The bounded buffer provides the right trade-off between performance and UX reliability.

## References
- ADR-020 (live tracking)
- `Infrastructure/Realtime/{SequencedTrackingEvent,RedisOrderTrackingBus}.cs`,
  `Controllers/OrderTrackingController.cs`
