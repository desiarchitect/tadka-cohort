# ADR-051: Dead-Letter Queue for Poison Messages

**Date:** 2026-07-13
**Status:** Accepted
**Deciders:** Tadka architecture team

## Context

`OrderPlacedConsumer` (Payment service) reads `order-placed` with manual offset commit: the
offset only advances after `HandleAsync` succeeds. Before this ADR, a message that threw during
processing (malformed JSON, a business exception) was caught, logged, and the loop moved on to
`consumer.Consume()` again — but Confluent.Kafka's `Consume()` advances the fetch position on
every call **regardless of commit**. We confirmed this behavior during a staging outage: after a poison message failed once,
placing one healthy order afterward committed offset 5 (past both messages), and the group's lag
returned to `0`. The poison message was not retried, not blocked, not queued — it was **silently
skipped forever**. The order it belonged to sat at `Created` permanently, never charged, with no
error anywhere a human would see it. This is a worse failure than "blocks the partition": it is
invisible data loss.

A second failure mode we want to prevent is a partition that genuinely cannot make progress — if every subsequent message also depended on
the poison one being processed first, or if there were no later traffic to trigger the skip, the
consumer would sit idle at that offset indefinitely. Either way — silent loss or indefinite idle —
neither is acceptable for a payment-adjacent event.

## Decision

`OrderPlacedConsumer` now tracks per-offset failure counts in-process (`PoisonMessageTracker`).
On a handler exception:

1. If the offset has failed fewer than `MaxAttempts` (3) times, explicitly `consumer.Seek()` back
   to that exact offset — forcing genuine redelivery on the next poll, not a silent skip — with a
   short delay (300ms) between attempts so a persistently broken message doesn't spin the CPU.
2. On the 3rd failure, publish a `DlqMessage` (original topic, the raw unmodified original
   payload, the error text, attempt count, timestamp) to `order-placed.dlq`, **then** commit the
   original offset — quarantining the poison message instead of losing it, and unblocking the
   partition for every order behind it.

Operational runbooks provide procedures for reading `order-placed.dlq`, extracting each `OriginalPayload`, and republishing it to `order-placed` once the
root cause is fixed. A message that's still broken simply fails 3 more times and lands back on
the DLQ.

## Consequences

### Positive
- A poison message is quarantined with its original payload and failure reason preserved for
  inspection, instead of being silently and permanently dropped.
- The partition unblocks after a bounded number of attempts — no indefinite idle, no unbounded
  retry storm.
- The retry-then-DLQ decision (`PoisonMessageTracker`) is a small, dependency-free class, unit
  tested without a live Kafka broker (`PoisonMessageTrackerTests.cs`).

### Negative
- The attempt counter is process-local (an in-memory `Dictionary`). A consumer restart before the
  limit is reached gives a message a fresh budget of attempts. Persisting attempt counts (e.g.
  in the Inbox table, or via message headers) is a future production hardening step.
- The 300ms backoff between retries is fixed, not exponential/jittered — fine for 3 attempts on
  one message; would need real backoff if `MaxAttempts` grew significantly.

### Risks
- A burst of poison messages (a bad deploy affecting every message, not one) would DLQ everything
  quickly — correct behaviour, but worth being ready for: the DLQ growing fast is itself the
  incident signal, requiring alerts on DLQ depth.

## Alternatives Considered

### Option A: No retry, DLQ on first failure
- Pros: simplest possible logic; avoids the Seek/backoff complexity entirely.
- Cons: loses the ability to self-heal from a genuinely transient failure (e.g. a momentary DB
  blip inside `ChargeAsync`) — every failure, transient or permanent, quarantines immediately.
- Why rejected: bounded-retry-then-DLQ adds little complexity for real self-healing on transient errors.

### Option B: Unbounded retry
- Pros: never loses a message.
- Cons: this is exactly the "blocks the partition forever" failure mode — one truly poison message (never fixable by retrying, e.g. permanently malformed JSON
  from a bug that already shipped) would stall every order behind it indefinitely.
- Why rejected: unbounded retry on a non-transient failure has no exit condition.

### Option C: A separate DLQ-routing consumer/process instead of inline handling
- Pros: cleaner separation of concerns; the main consumer stays simple.
- Cons: added infrastructure footprint (another process, another failure mode) for the current scale.
- Why rejected: the inline version handles the mechanism just as clearly with less to run. Revisit if DLQ logic needs to grow (e.g. per-topic
  routing rules, alerting integration).

## References
- ADR-027 (Kafka async event backbone), ADR-028 (transactional outbox/inbox) — the delivery
  guarantee this DLQ sits on top of.
- ADR-050 (event envelope versioning) — a related but distinct failure class.
- `src/Tadka.Payment.Api/Messaging/PoisonMessageTracker.cs`, `OrderPlacedConsumer.cs`.
