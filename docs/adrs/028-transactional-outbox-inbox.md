# ADR-028: Transactional Outbox + Inbox (durable publish, idempotent consume)

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Kafka (ADR-027) decouples services in time, but it introduces two classic correctness gaps:

1. **The dual-write problem (producer side).** "Save the order to Postgres, then publish `order-placed` to Kafka" is **two writes to two systems with no shared transaction**. If the process crashes between them, the order exists but the event never publishes → the order is never charged (a silent lost charge — exactly Day-8's wound, just relocated). You cannot atomically commit a DB row *and* a Kafka message.
2. **At-least-once redelivery (consumer side).** Kafka redelivers on a crash-before-offset-commit, so a consumer can see the same `order-placed` twice → a double charge if naive.

## Decision

**Outbox on the producer, Inbox on the consumer.**

- **Transactional Outbox (monolith):** when an order is placed, write the `order-placed` payload into an `outbox_messages` row **in the same database transaction as the order**. One commit, atomic — either both land or neither. A separate **OutboxRelay** `BackgroundService` **claims** unsent rows, publishes them to Kafka, and marks them sent (at-least-once: if it crashes after publish before marking, it republishes — the consumer's Inbox dedups). The event can never be lost because it's committed with the order.
- **Multi-instance claim (`FOR UPDATE SKIP LOCKED`):** the monolith may run on **N pods**, so N relays poll the same table. A naive `WHERE ProcessedAt IS NULL … LIMIT 50` lets every pod grab the **same** rows → duplicate Kafka publishes + Postgres lock contention. The relay therefore claims each batch inside a transaction with `SELECT … FOR UPDATE SKIP LOCKED`: the row locks are held until commit, so other pods **skip the locked rows** and take the next *disjoint* batch. The Inbox still makes a stray duplicate *correct*, but SKIP LOCKED makes duplicates *rare and cheap* instead of guaranteed.
- **Inbox / idempotent consumer (Payment + monolith):** before processing a message, record its `messageId` in an `inbox_messages` table; if it's already there, **skip** (it's a redelivery). Combined with the existing one-charge unique index on `payment.order_id`, this gives an **exactly-once *effect*** on top of at-least-once *delivery* — without Kafka transactions.

## Consequences

### Positive
- **No lost events** (outbox is committed with the business write) and **no duplicate side-effects** (inbox + unique index). The two correctness gaps Kafka opened are closed.
- The outbox table is also a built-in **audit log** of what was emitted, and a natural place to add retry/DLQ bookkeeping later.

### Negative / Risks
- **More moving parts:** an outbox table + a relay loop + an inbox table per consumer. Polling adds a little latency (mitigated by a short poll interval / notify).
- The relay is itself at-least-once → consumers **must** be idempotent (that's the Inbox's job; don't skip it).
- **Concurrency assumption:** correctness across N relay instances depends on the `FOR UPDATE SKIP LOCKED` claim above. The single-instance shortcut (plain `LIMIT`) is the classic mistake — it *looks* fine on one pod and silently double-publishes the moment you scale out. The three ways to make the relay multi-instance-safe: (1) **`SKIP LOCKED`** claim (what we do — right-sized, no new infra); (2) **leader election** (only one pod runs the relay — simpler reasoning, but a SPOF until failover); (3) **CDC/Debezium** (no app-side relay at all — read the WAL). Pick by scale.
- Inbox/outbox tables grow → need periodic pruning (a housekeeping job; noted, not built today).
- **Publish happens inside the claiming transaction**, so the `FOR UPDATE SKIP LOCKED` row lock stays open for as long as the Kafka publish takes. Kept for simplicity here, but it means a slow/unreachable broker holds that lock. We bound the damage with `MessageTimeoutMs`/`RequestTimeoutMs` (10 s) on the producer instead of librdkafka's 300 s default, so a broker outage holds the lock for single-digit seconds, not five minutes. The production-grade options, if this needs to scale further: (1) a **two-phase claim** — set `LockedUntil` and commit, publish outside any open transaction, then a second short transaction stamps the row sent (releases the row lock immediately, at the cost of a brief window where a crash mid-publish needs the existing Inbox dedup to save it); or (2) **CDC**, which removes the app-side publish-in-transaction step entirely.

### Cost (₹ / effort)
Pure code + two small tables; no new infra beyond Kafka. The saving is correctness on money — no lost or double charges — which is non-negotiable for payments.

## Alternatives Considered
- **Direct publish after SaveChanges (dual write):** the naive baseline we're fixing — loses events on a crash. Rejected.
- **Listen-to-yourself / CDC (Debezium on the WAL):** robust, no app-side relay, but adds Debezium + Kafka Connect infra. Overkill for Tadka now; the polling outbox is the right-sized step. Revisit at high volume.
- **Exactly-once via Kafka transactions:** rejected in ADR-027 (complexity trap); outbox + inbox achieves the same *effect* more simply.

## Cross-stack equivalents
Outbox + Inbox are patterns, not tools: **MassTransit/NServiceBus** ship both out of the box (.NET); **Spring Modulith / Spring's transactional outbox**, or **Debezium** CDC (Java/any); a hand-rolled table + relay in **Node/Go**. The idempotent-consumer (inbox) idea = a processed-message-id table or a Redis `SETNX` dedup anywhere. The principle — *commit the event with the business data; dedup on consume* — is universal.

## References
- ADR-011 (idempotency-key — the same idea, client side), ADR-023 (the non-durable in-memory queue this replaces), ADR-027 (Kafka), ADR-029 (Saga)
- `cohort-prep/day-09/break-kit-day-09.md` (outbox crash-safety; redelivery → one charge)
- Implementation: monolith `Data/Outbox/*` + `OutboxRelay`; both consumers' `inbox_messages` + dedup check

## Revisit When
At high volume, replace the polling relay with **CDC (Debezium)** or adopt **MassTransit**'s outbox. Add an outbox/inbox **pruning** job. Add a **DLQ** when a real poison-message case appears (Week 5 stretch / Week 7 resilience).
