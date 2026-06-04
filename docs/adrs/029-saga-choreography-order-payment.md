# ADR-029: Saga (Choreography) for the Order↔Payment Distributed Transaction

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

In the monolith, "create order + charge payment" was one **local database transaction** — if payment failed, everything rolled back. Now order and payment live in **separate services with separate databases** (ADR-024/026), so there is no transaction spanning both. We can end up in an inconsistent state: order created, payment failed (or vice-versa). This is the **distributed transaction problem**, and **two-phase commit (2PC) is not the answer** — it's slow, blocks resources, and doesn't scale across independently-deployed services.

## Decision

**Model order↔payment as a Saga, using choreography** (no central orchestrator):

1. Order created (`pending`) → monolith emits `order-placed` (via the Outbox, ADR-028).
2. Payment service consumes it, performs its **local transaction** (charge + persist), and emits `payment-results` (`Completed`/`Failed`).
3. Monolith consumes the result and runs its **local transaction**: `Completed` → confirm the order; `Failed` → **compensating action**: cancel the order (the Day-7/8 `PaymentFailed` → `order.Cancel()` reaction, now driven by a Kafka event).

Each step is a local ACID transaction; cross-step consistency is **eventual**, reached by events + compensations. **Choreography** (services react to events) is chosen over **orchestration** (a central coordinator) because there are only two participants and the flow is linear — a coordinator would be ceremony. We note the trade-off: choreography's flow is implicit (you read it across handlers), which gets hard to follow as participants grow.

## Consequences

### Positive
- **No 2PC, no distributed locks** — each service commits independently and stays autonomous.
- Compensation (cancel-on-failure) is explicit and already modelled by the order state machine — the Saga just drives it via events.
- Naturally durable + idempotent on top of Outbox/Inbox (ADR-028).

### Negative / Risks
- **Eventual consistency window:** the order is briefly `pending` before it converges to `Confirmed`/`Cancelled` (surfaced live on the Day-6 SSE stream).
- **Choreography is implicit:** with many participants the end-to-end flow is hard to trace (→ a light trace view now, full observability Week 7). At that point, consider orchestration.
- **Compensations are business logic, not rollbacks:** "cancel the order" may need to also refund a successful charge in richer flows (the "timeout ≠ didn't charge" reconciliation problem, ADR-023) — modelled as a compensating event, not a DB rollback.

### Cost (₹ / effort)
Code only (event handlers + the existing state machine). The saving: correctness across services without the latency/fragility of 2PC.

## Alternatives Considered
- **2PC / distributed transactions:** rejected — blocking, slow, poor availability, doesn't fit independently-deployed services.
- **Orchestration (a saga coordinator / state machine service):** better for *complex, many-step* sagas (clear central flow, easier to trace/operate). Overkill for two linear participants today; revisit when Delivery/Restaurant join the flow.
- **No saga (hope it succeeds):** leaves inconsistent state on partial failure. Rejected.

## Cross-stack equivalents
Saga is a pattern: choreography = services reacting to domain events anywhere (**Spring** `@KafkaListener` + events, **NestJS** event handlers, **Go** consumers). Orchestration frameworks: **MassTransit Saga State Machine / NServiceBus Sagas** (.NET) ≈ **Axon / Camunda / Temporal** (Java/poly) ≈ **Temporal** (Go/poly). Compensating transactions are the universal alternative to 2PC for cross-service consistency.

## References
- ADR-024/026 (separate service + DB — why no shared transaction), ADR-027 (Kafka), ADR-028 (Outbox/Inbox), ADR-012/013 (order state machine + events the compensation reuses)
- `cohort-prep/day-09/break-kit-day-09.md` (decline → compensating cancel)
- Implementation: the `payment-results` consumer → `PaymentCompletedEvent`/`PaymentFailedEvent` → existing order reaction handlers

## Revisit When
When a third participant joins the order flow (Delivery/Restaurant, Week 6) and the choreography becomes hard to follow → consider **orchestration** (a saga coordinator, e.g. MassTransit state machine / Temporal). When compensation must refund real charges → model the refund saga explicitly.
