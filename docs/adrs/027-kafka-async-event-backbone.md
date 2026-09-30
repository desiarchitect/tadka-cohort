# ADR-027: Kafka as the Asynchronous Event Backbone (replace the synchronous HTTP bridge)

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Day 8 extracted Payment and bridged it over **synchronous HTTP** (ADR-025), leaving a deliberate wound: when the Payment service is down, the monolith's call fails, the queued charge is **lost**, and the order strands in `pending`. The in-memory queue (ADR-023) isn't durable either — a monolith crash loses queued work. Both are *temporal coupling*: producer and consumer must be alive at the same instant.

At Week-5 scale (a viral moment, sharp spikes) this is the failure that cascades: a synchronous call chain means one slow/again-down dependency drags the whole flow, and naive retries double-charge. We need a transport that **decouples in time**: the producer hands off and moves on; the consumer processes when it can; nothing is lost if it's down.

## Decision

**Introduce Kafka as the async backbone for cross-service *commands/events*.** The monolith publishes `order-placed`; the Payment service consumes it, charges, and publishes `payment-results`; the monolith consumes that and converges the order. **HTTP stays for request/reply *queries*** (`GET /payments/{orderId}`) — Kafka is for the fire-and-forget event flow, not synchronous lookups.

- **Delivery semantics: at-least-once + idempotent consumers.** Commit the offset *after* processing; on a crash, Kafka redelivers; the consumer dedups (Inbox, ADR-028) and the one-charge unique index makes a redelivery a no-op. We do **not** use exactly-once/Kafka transactions — it couples consumer logic to the transaction coordinator and is a complexity trap most teams get wrong. At-least-once + idempotency is the industry default.
- **Partitioning by `orderId`** (key) so all events for one order stay ordered on one partition; parallelism across orders.
- **Library:** raw **Confluent.Kafka** to show the mechanics (topics, keys, offsets, consumer groups). **In production, MassTransit/NServiceBus** wrap this with outbox/retries/DLQ — named in the cohort, used at work.

## Consequences

### Positive
- **A down service no longer loses work** — messages wait in the topic and the consumer catches up (lag drains). The Day-8 wound is healed.
- Decoupled in time + space; replayable; fan-out ready (a future Notification/Analytics consumer just subscribes).
- Order intake stays in milliseconds (it always did since Day 7); now settlement is durable too.

### Negative / Risks
- A **broker to operate** (partitions, retention, consumer-group rebalancing, lag monitoring) — real ops weight.
- **Eventual consistency** is now explicit and visible (an order is `pending` until the consumer settles it).
- At-least-once means **every consumer must be idempotent** (more code) — addressed by the Inbox (ADR-028).
- Ordering/observability across async hops is harder → a light trace view now, full OpenTelemetry in Week 7.

### Cost (₹ / effort)
A Kafka broker (one container locally; a managed MSK/Confluent cluster in prod — *not* free, justify it). The saving: no lost charges, no cascading synchronous outages at spike — directly protects revenue at the viral-moment milestone.

## Alternatives Considered
- **Keep synchronous HTTP:** the trap — temporal coupling, lost work, cascades at scale. Rejected (it's the failure we're fixing).
- **RabbitMQ / SQS:** valid brokers; queue semantics differ (no log/replay/consumer-group fan-out the way Kafka does). Kafka chosen for the replay + multi-consumer story Tadka grows into; the *pattern* is broker-agnostic.
- **Exactly-once (Kafka transactions):** complexity trap across a .NET consumer + Postgres; rejected for at-least-once + idempotency.

## Cross-stack equivalents
Kafka itself is language-neutral. Clients: **Confluent.Kafka** (.NET) ≈ **spring-kafka** / Java client (Java) ≈ **kafkajs** (Node) ≈ **segmentio/kafka-go** or **confluent-kafka-go** (Go). Higher-level frameworks: **MassTransit/NServiceBus** (.NET) ≈ **Spring Cloud Stream** (Java) ≈ NestJS microservices transport (Node). At-least-once + idempotent-consumer is the same doctrine everywhere.

## References
- ADR-023 (in-memory queue — the non-durable predecessor), ADR-025 (the HTTP bridge this replaces for events), ADR-028 (Outbox + Inbox), ADR-029 (Saga)
- `cohort-prep/day-09/break-kit-day-09.md` (consumer-down catch-up; redelivery → one charge)
- Implementation: monolith `Infrastructure/Messaging/*` (producer + results consumer), Payment service `Messaging/*` (order-placed consumer + results producer)

## Security addendum: authenticated broker (SASL/SCRAM-SHA-256)

**Decision.** The local broker requires SASL/SCRAM-SHA-256 on the `HOST` (apps, CLI) and `DOCKER` (Kafka UI) listeners. The `CONTROLLER` listener stays `PLAINTEXT`: it is KRaft quorum traffic on a single-voter broker with no published port. One shared user (`tadka`) is used by every client. Each service reads `Kafka:SaslUsername` / `Kafka:SaslPassword` and applies them to every producer and consumer through one `KafkaSecurity.ApplySasl(...)` extension in its own `Messaging.cs` (services do not share code, ADR-024/026). The extension does nothing unless a username is configured, so the test suite (Testcontainers Kafka, no auth) and any un-secured broker are unaffected.

**Why not just document the gap.** An unauthenticated broker lets any process on the network read or write any topic, including `order-placed`. The teaching stack should not model that as normal.

**How the broker gets its user.** The `apache/kafka` image cannot seed a SCRAM credential from environment variables: the credential must be written into the cluster metadata when storage is formatted (`kafka-storage.sh format --add-scram`, KIP-900). The image's own start-up formats storage as a side effect of writing `server.properties`, so `docker/kafka-scram-entrypoint.sh` lets that step run, wipes the freshly formatted log (the container has no persistent volume), and formats again with `--add-scram` before starting the broker. It also writes `/etc/kafka/docker/client.properties`, which the CLI tools take through `--command-config` (`kafka-topics.sh`, `kafka-consumer-groups.sh`), `--producer.config` (`kafka-console-producer.sh`) or `--consumer.config` (`kafka-console-consumer.sh`).

**Trade-offs and limits.** `SASL_PLAINTEXT` authenticates but does not encrypt; production needs `SASL_SSL`. One shared user and no ACLs means any client can touch any topic; real isolation is a user per service plus ACLs. The password is a demo default committed in `appsettings.Development.json` and `docker-compose.yml`; production reads it from a secrets manager. **Not covered:** the Azure/cloud Kafka (`deploy/azure`), a separate plain container reachable only inside the private Container Apps network; securing it needs a custom image carrying the same bootstrap script and Terraform secret plumbing.

**Revisit when** the broker is exposed beyond a single trusted network, or a second team owns a consumer: move to `SASL_SSL` and per-service users with ACLs.

## Revisit When
When fan-out grows (Notification, Analytics, Delivery consumers) — revisit topic design + schema registry (event versioning). When ops burden justifies it, move from raw Confluent.Kafka to **MassTransit** (outbox/retry/DLQ built in). DLQ for poison messages is a Week-5 stretch → formalize when a real poison case appears.
