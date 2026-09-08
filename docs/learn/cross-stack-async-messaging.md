# Learn: building this in Java or Node — what actually changes

> **Weekday reading for Day 9.** In class we built Kafka + Outbox/Inbox + Saga in .NET with raw
> `Confluent.Kafka`. If you'd build the same system in Java or Node, the *pattern* is identical —
> at-least-once delivery, an idempotent consumer, a durable outbox, choreographed compensation.
> What changes is the tooling and a handful of stack-specific traps. This page is not code —
> it's the questions you'd actually have to answer while wiring this up in another stack.

## 1. The Kafka client itself

**The question, in any language:** how does the consumer commit its offset, and is that commit
tied to "the message was successfully processed" or just "the message was received"? Get this
wrong and you silently get at-most-once (lose work on crash) instead of the at-least-once we
built on `day-09`.

**Java (Spring Boot / spring-kafka):** Spring's auto-configuration defaults to `AckMode.BATCH`
with `enable-auto-commit=true` — the framework commits on a timer, not after your handler
finishes. That's the exact trap: your handler can throw *after* the offset already committed.
You have to explicitly set `AckMode.MANUAL` (or `MANUAL_IMMEDIATE`) and call `ack.acknowledge()`
yourself, at the point in your code equivalent to where `PaymentResultsConsumer.HandleAsync`
calls `SaveChangesAsync` on the Inbox row — after the side effect, not before.

**Node (kafkajs):** `autoCommit` defaults to `true` on a timer, same trap as Spring's default.
Set `autoCommit: false` and call `heartbeat()`/manual `commitOffsets()` after your `eachMessage`
handler completes. kafkajs also gives you `eachBatch` for finer control if you need to commit
mid-batch on partial failure — worth knowing exists, rarely worth reaching for on day one.

**What doesn't change:** the partition key discipline (`orderId`, so the same order's events
stay ordered) is a Kafka protocol-level decision, not a library one. Consumer groups work
identically regardless of client language — a Java consumer and a .NET consumer in the *same*
group would split partitions between them exactly like `tadka-payment`'s single consumer does
today.

## 2. Transactional Outbox + Inbox

**The question, in any language:** you can't atomically write a business row and publish a Kafka
message. Something has to bridge that gap — and whatever bridges it needs its own atomicity
story when you run more than one instance.

**Java:** two real options, both stronger than what we hand-rolled in .NET.
- **Debezium (CDC).** Debezium is JVM-native and reads the Postgres WAL directly via logical
  replication — no polling relay at all, no `SELECT ... FOR UPDATE SKIP LOCKED` to get right,
  because there's no relay racing against itself. This is the option ADR-028 names as the
  "high-volume" upgrade path, and Java is where it's most natural to reach for because Kafka
  Connect (which runs Debezium) is a JVM ecosystem tool.
- **Spring Modulith's Event Publication Registry.** If you're already publishing in-process
  events via `ApplicationEventPublisher` (Spring's equivalent of MediatR), Spring Modulith gives
  you outbox semantics almost for free — it persists the event alongside your transaction and
  replays anything not yet delivered on restart.
- If you hand-roll a relay anyway: `SELECT ... FOR UPDATE SKIP LOCKED` is expressible via a
  native query or `@Lock(LockModeType.PESSIMISTIC_WRITE)` with Hibernate's lock-mode hints — the
  same claim discipline `OutboxRelay.cs` uses, just JPQL instead of EF Core's raw SQL.

**Node:** no framework gives you outbox-for-free the way Spring Modulith does. You hand-roll it,
same shape as the .NET version: write the business row and an outbox row in one transaction
(`pg` or Prisma), then a scheduled job (`node-cron`, or a BullMQ repeatable job if you want
retry/backoff built in) claims and publishes unsent rows. One real gotcha: as of most common
Prisma versions, `SKIP LOCKED` isn't expressible through the query builder — you drop to
`$queryRaw` for the claim query, same reason our C# uses raw-ish EF for it. Debezium is still an
option here too — it doesn't care what language the *producer* is written in, since it reads the
database's WAL, not your app's code.

**What doesn't change:** the multi-instance danger is identical everywhere — a naive
`WHERE processed_at IS NULL LIMIT 50` will let N pods claim the same rows in *any* stack. The
fix (SKIP LOCKED, leader election, or CDC) is an architectural choice independent of language.

## 3. Saga (choreography vs. orchestration)

**The question, in any language:** do participants react to each other's events with no central
coordinator (choreography), or does one component drive the whole flow (orchestration)? This is
purely about how many participants there are and how linear the flow is — not a language
decision.

**Choreography** is the same shape everywhere: a consumer that reacts to one topic and
(optionally) publishes to another. Spring's `@KafkaListener`, kafkajs's `eachMessage` callback,
and .NET's `PaymentResultsConsumer` are the same pattern in three syntaxes.

**Orchestration**, if you outgrow two linear participants (our Week-6 trigger: Delivery or
Restaurant joins the flow):
- **Java** has the most mature options in this space — **Camunda** (BPMN-based, visual workflow
  definitions, common in enterprise Java shops), **Axon Framework** (event-sourcing + saga
  support built into one framework), or **Temporal** (polyglot, has a first-class Java SDK).
- **Node** has a first-class **Temporal** SDK too, or NestJS's `@nestjs/cqrs` module ships its
  own saga abstraction if you're already in that ecosystem.

**The actual decision, regardless of stack:** is the flow linear with 2-3 participants (stay
choreographed) or does it branch, need human intervention, or require a visual audit trail for
ops (orchestration earns its ceremony)? That's ADR-029's own "Revisit When" — a 3rd participant
is the trigger, not a language migration.

---

**One thing to notice across all three sections:** almost nothing above is really a "Java vs.
Node vs. .NET" decision. It's "does the ecosystem hand you a tool for this problem, or do you
hand-roll it" — Java's Kafka Connect/Debezium and Spring Modulith genuinely give you more for
free than .NET or Node do here. That's worth knowing before you assume "we'll rebuild this in
Java, so it'll need less code" — sometimes true, sometimes not, and the only way to know is to
ask the question above for your specific stack's ecosystem maturity.
