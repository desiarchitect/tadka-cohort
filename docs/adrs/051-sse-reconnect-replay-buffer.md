# ADR-051: SSE Reconnect Replay — a Bounded Buffer, Not Durability

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
  channel — so a client reconnecting immediately after a publish can never see a seq in the live
  stream that isn't already in the buffer it would replay from.
- Every SSE frame carries `id: {seq}` (the standard SSE field browsers use to auto-populate
  `Last-Event-ID` on their own native reconnect, even though this client uses `fetch` and sets it
  explicitly).
- On reconnect with `Last-Event-ID`, the endpoint replays every buffered event with a higher seq,
  THEN resumes live streaming — de-duplicating against the replay using the same seq (the live
  subscription starts before the replay read, so the same event can arrive both ways in a narrow
  race window; anything not newer than what was just replayed is dropped).

## Consequences

### Positive
- A dropped connection during a status change no longer loses that status — the client catches up
  automatically on reconnect, no different UX code path from "was always connected."
- The mechanism is the standard SSE reconnect contract (`Last-Event-ID`), not a bespoke protocol —
  transferable knowledge, and free interop with any SSE client that follows the spec.

### Negative
- **This is a buffer, not durability.** 20 events / 6 hours are hard limits: a client offline
  longer than that, or an order with more than 20 transitions in its history, loses events beyond
  the window with no error — it just resumes from whatever is still buffered. The buffer's job is
  covering realistic reconnect gaps (a network blip, a phone locked for a few minutes), not
  arbitrary outages.
- Every publish now costs 3 Redis operations (INCR, RPUSH+LTRIM, PUBLISH) instead of 1 — a real,
  small cost for a real capability.
- No cross-instance ordering guarantee beyond what Redis itself provides — acceptable, since a
  single order's events are always sequenced from the SAME counter regardless of which app
  instance handled the transition that produced them.

### Risks
- A silent gap (client offline longer than the buffer holds) still fails invisibly — the client
  has no way to know it missed something beyond the buffer. **Mitigation:** none needed today
  (the UI already re-fetches current status on connect regardless, so the end state is always
  eventually correct — only the INTERMEDIATE states are what a long gap loses); worth revisiting
  if intermediate-state history ever needs to be provably complete.

## Alternatives Considered

### Option A: No replay — always start fresh on reconnect (status quo before this ADR)
- Simplest; already shipped as ADR-020's original behavior.
- Rejected as the demonstrated BREAK: a reconnect during a transition silently skips states, with
  no error and no indication anything was missed — invisible data loss in a customer-facing UI.

### Option B: Durable delivery via the transactional outbox (Week 5's pattern), applied here
- Would eliminate the buffer's window entirely (true no-loss delivery).
- Rejected for Day 6: the outbox pattern is deliberately introduced in Week 5 once Kafka exists as
  the durable log to write to — pulling it forward here to fix a UI nicety would blur that later,
  much bigger lesson (guaranteed at-least-once delivery across service boundaries) with a much
  smaller one (don't lose the last few seconds of a live UI stream). The buffer is the
  RIGHT-SIZED fix for what this actually is: a live status feed, not a payment record.

## References
- ADR-020 (live tracking — what this extends), ADR-028 (transactional outbox, Week 5 — the
  durable-delivery answer this deliberately is NOT, see Alternatives)
- `Infrastructure/Realtime/{SequencedTrackingEvent,RedisOrderTrackingBus}.cs`,
  `Controllers/OrderTrackingController.cs`
- Break kit: `cohort-prep/day-06/break-kit-day-06.md`

## Revisit When
A feature ever needs PROVABLY complete event history (not just "usually catches up") — that is
the trigger to reach for the Week 5 outbox pattern instead of a bigger buffer.
