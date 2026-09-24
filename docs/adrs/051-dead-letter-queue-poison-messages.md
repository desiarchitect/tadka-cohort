# ADR-051: Dead-Letter Queue for Poison Messages

**Date:** 2026-07-13
**Status:** Accepted
**Deciders:** Tadka architecture team

## Context

`OrderPlacedConsumer` (Payment service) reads `order-placed` with manual offset commit: the
offset only advances after `HandleAsync` succeeds. Before this ADR, a message that threw during
processing (malformed JSON, a business exception) was caught, logged, and the loop moved on to
`consumer.Consume()` again — but Confluent.Kafka's `Consume()` advances the fetch position on
every call **regardless of commit**. We confirmed this live: after a poison message failed once,
placing one healthy order afterward committed offset 5 (past both messages), and the group's lag
returned to `0`. The poison message was not retried, not blocked, not queued — it was **silently
skipped forever**. The order it belonged to sat at `Created` permanently, never charged, with no
error anywhere a human would see it. This is a worse failure than "blocks the partition": it is
invisible.

A second failure mode we did *not* find (but which the original design intended to guard against)
is a partition that genuinely cannot make progress — if every subsequent message also depended on
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

Two operator scripts complete the loop: `scripts/inject-poison.ps1` (simulates a bad deploy
landing a malformed or schema-broken message) and `scripts/replay-dlq.ps1` (reads
`order-placed.dlq`, extracts each `OriginalPayload`, and republishes it to `order-placed` once the
root cause is fixed — a message that's still broken simply fails 3 more times and lands back on
the DLQ, which is the correct, safe outcome).

## Consequences

### Positive
- A poison message is quarantined with its original payload and failure reason preserved for
  inspection, instead of being silently and permanently dropped.
- The partition unblocks after a bounded number of attempts — no indefinite idle, no unbounded
  retry storm.
- The retry-then-DLQ decision (`PoisonMessageTracker`) is a small, dependency-free class, unit
  tested without a live Kafka broker (`PoisonMessageTrackerTests.cs`), matching the project's
  "tests stay green without Kafka" rule.

### Negative
- The attempt counter is process-local (an in-memory `Dictionary`). A consumer restart before the
  limit is reached gives a message a fresh budget of attempts — documented, not hidden, and an
  acceptable trade-off for a single-instance teaching deployment. Persisting attempt counts (e.g.
  in the Inbox table, or via message headers) is the real production hardening, named below.
- The 300ms backoff between retries is fixed, not exponential/jittered — fine for 3 attempts on
  one message; would need real backoff if `MaxAttempts` grew significantly.
- The DLQ is not itself monitored/alerted on in this project (no Day-13 dashboard panel) —
  something landing on `order-placed.dlq` is silent unless an operator goes looking.

### Risks
- A burst of poison messages (a bad deploy affecting every message, not one) would DLQ everything
  quickly — correct behaviour, but worth being ready for: the DLQ growing fast is itself the
  incident signal, and today nothing pages on it.
- **A short, transient Postgres outage looks identical to a genuinely poison message.** 3 attempts
  at a fixed 300ms gap is ~1 second total — a Postgres restart, a brief connection-pool exhaustion,
  or a short network blip inside `ChargeAsync` routinely outlasts that window. Real, healthy orders
  DLQ alongside truly malformed ones, with no distinction in the DLQ payload. Revisit: exponential
  backoff (so a multi-second outage gets a real chance to recover before quarantine), or classify
  the exception — don't count infra/transient errors (`DbUpdateException` from a dropped
  connection, `TimeoutException`) toward the poison-attempt counter the same way a permanent
  deserialization/business failure counts.

## Alternatives Considered

### Option A: No retry, DLQ on first failure
- Pros: simplest possible logic; avoids the Seek/backoff complexity entirely.
- Cons: loses the ability to self-heal from a genuinely transient failure (e.g. a momentary DB
  blip inside `ChargeAsync`) — every failure, transient or permanent, quarantines immediately.
- Why rejected: the bounded-retry-then-DLQ shape is the standard, teachable pattern, and costs
  little extra code for real self-healing on transient errors.

### Option B: Unbounded retry (the pre-fix behaviour, but with an explicit Seek added)
- Pros: never loses a message.
- Cons: this is exactly the "blocks the partition forever" failure mode the DLQ pattern exists to
  avoid — one truly poison message (never fixable by retrying, e.g. permanently malformed JSON
  from a bug that already shipped) would stall every order behind it indefinitely.
- Why rejected: unbounded retry on a non-transient failure has no exit condition.

### Option C: A separate DLQ-routing consumer/process instead of inline handling
- Pros: cleaner separation of concerns; the main consumer stays simple.
- Cons: real added infrastructure (another process, another failure mode) for a cohort-scale
  teaching system; the inline version demonstrates the mechanism just as clearly with less to run.
- Why rejected: proportionate to project scale; revisit if DLQ logic needs to grow (e.g. per-topic
  routing rules, alerting integration).

## Teaching fields

- **Topic:** poison-message handling and dead-letter queues for an at-least-once Kafka consumer.
- **Options:** no retry + immediate DLQ | unbounded retry (no exit) | bounded retry then DLQ (chosen) | separate DLQ-routing process.
- **Choice:** bounded retry (3 attempts, explicit `Seek`) then DLQ + commit.
- **Why:** self-heals transient failures without giving a genuinely poison message the power to
  stall the partition forever, and keeps the mechanism inline and simple at this project's scale.
- **Trade-off:** the attempt counter resets on a consumer restart (process-local state) — an
  honest, named limitation, not a silent gap.
- **Failure mode** (2 AM during dinner rush): a bad deploy makes every `order-placed` message fail
  (e.g. a bug in `ChargeAsync` itself, not the message). Every message DLQs after 3 attempts
  within ~900ms each — the partition stays healthy, but the DLQ fills fast and payments silently
  stop happening for everyone, with nothing paging on it. The gap: this project has no DLQ-depth
  alert (Day 13's dashboards don't cover it) — a real production deployment would need one.
- **Revisit when:** the DLQ needs to be monitored/alerted (a Day-13-style Prometheus counter on
  `order-placed.dlq` production rate would be the natural next step), or when attempt counts need
  to survive a restart (move the counter into the Inbox table or a Kafka message header instead
  of in-memory).
- **Cross-stack equivalents:** Spring Kafka has this pattern built in (`DeadLetterPublishingRecoverer`
  + `SeekToCurrentErrorHandler`, or newer `DefaultErrorHandler` with backoff); Node's `kafkajs` has
  no built-in DLQ helper — this exact hand-rolled retry-then-republish shape is the idiomatic
  approach; Go's `segmentio/kafka-go` or `confluent-kafka-go` likewise expect this to be hand-rolled
  around `SetOffset`/`Seek`.

## References
- ADR-027 (Kafka async event backbone), ADR-028 (transactional outbox/inbox) — the delivery
  guarantee this DLQ sits on top of.
- ADR-050 (event envelope versioning) — a related but distinct failure class: messages that DO
  throw (this ADR) vs. messages that silently corrupt data without throwing (ADR-050).
- `cohort-prep/day-09/break-kit-day-09.md` Beat 6 — the captured before/after evidence.
- `src/Tadka.Payment.Api/Messaging/PoisonMessageTracker.cs`, `OrderPlacedConsumer.cs`.
- `scripts/inject-poison.ps1`, `scripts/replay-dlq.ps1`.
