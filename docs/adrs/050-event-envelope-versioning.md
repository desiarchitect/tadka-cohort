# ADR-050: Event Envelope Versioning (Additive-Only Schema Evolution)

**Date:** 2026-07-13
**Status:** Accepted
**Deciders:** Tadka architecture team

## Context

`order-placed` (ADR-027) is a cross-service contract between Ordering and Payment, each owning
its own copy of the `OrderPlacedMessage` record (no shared library, by design). The two services
deploy independently. In production, a producer-side change will inevitably ship before every consumer has
caught up, or a consumer will be rolled back while a newer producer is already live. The question
is not whether the schema changes, but whether a change that lands out of order breaks anything —
and, worse, whether it breaks *loudly* or *silently*.

We encountered a scenario in staging: a message with a genuinely new,
unknown field (`PromoCode`) against the current consumer code processed without any
error — `System.Text.Json`'s default behaviour ignores unmapped JSON properties. We also witnessed
a case where `Currency` was renamed to `CurrencyCode` (a producer dropping a
field without warning). That did **not** throw either: the missing constructor parameter bound to
`null`, and a Postgres column default (`HasDefaultValue("INR")` on the payments table) silently
filled it in on `INSERT`. The payment succeeded with a currency nobody asked for, and nothing —
not the consumer, not the database, not a log line — surfaced the mistake.

## Decision

Schema evolution on `order-placed` (and by extension `payment-results`) is **additive-only**:

- New fields may be appended with a default value (e.g. `int Version = 1` on the envelope today).
- An existing field is **never** renamed or removed. A rename is treated the same as a removal.
- The `Version` field on the envelope is informational — for humans reading logs and DLQ payloads
  — not a runtime dispatch switch. There is exactly one message shape at a time; version bumps
  document that the shape gained a field, not that two shapes are simultaneously supported.

This is enforced by **engineering discipline and code review**, not by a runtime guard, because the failure
mode we found (a silent database-default fallback) proved there is no reliable runtime signal to
catch a rename — the message deserializes, the charge completes, the row gets written.

## Consequences

### Positive
- Additive changes (the common case: a new optional field) require zero coordination between
  Ordering and Payment — either side can deploy first.
- The `Version` field gives every message a self-documenting envelope version for log correlation
  and DLQ triage (ADR-051), without adding branching logic to the consumer.

### Negative
- The discipline has no automated enforcement in this codebase (no contract test comparing
  producer and consumer shapes across services). A determined mistake still ships.
- Because renaming/removing a field does not throw, a violation of this rule is invisible until
  wrong data surfaces downstream — which can be much later.

### Risks
- A rename silently defaults a value instead of failing. Mitigated today
  only by this ADR's discipline + PR review; a stronger mitigation (schema registry, contract
  tests) is named below as the real fix at scale.

## Alternatives Considered

### Option A: A runtime schema validator (e.g. reject unknown/missing fields explicitly)
- Pros: would catch the exact rename bug we found, loudly, at the point of deserialization.
- Cons: adds a dependency and coupling between two services that deliberately don't share code;
  over-engineered for our current team size.
- Why rejected: the discipline (never rename, always additive) removes the need for it at this
  scale; revisit if the number of producers/consumers grows past what the core team can review.

### Option B: A schema registry (e.g. Confluent Schema Registry, Avro/Protobuf contracts)
- Pros: compile-time or registration-time compatibility checking,
  used by every large Kafka deployment.
- Cons: infrastructure to run and operate, plus a serialization format migration (JSON to
  Avro/Protobuf) — disproportionate for two services and one event type right now.
- Why rejected today: defer until the number of event types or independently-deployed consumers grows enough
  that "everyone reviews carefully" stops scaling.

### Option C: Do nothing, assume JSON's default leniency is "good enough"
- Pros: zero work.
- Cons: "good enough" quietly produces wrong data in cases like field renames, which is worse than an explicit rule that at least everyone agrees
  to follow.
- Why rejected: the staging incident is precisely the risk this ADR exists to prevent.

## References
- ADR-027 (Kafka async event backbone) — the topic this envelope rides on.
- ADR-028 (transactional outbox/inbox) — the delivery guarantee this versioning policy assumes.
- ADR-051 (dead-letter queue) — the runtime safety net for messages that DO throw (malformed
  JSON).
