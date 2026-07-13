# ADR-045: Restaurant Reject + Compensating Refund (Saga completion)

**Date:** 2026-07-13  
**Status:** Accepted  
**Deciders:** Tadka Engineering Team

## Context

Day 9 modelled order↔payment as a choreographed Saga (ADR-029): payment `Failed` → cancel the order. That path never charged money.

Day 11 adds a second failure mode that **does** charge money first:

1. Payment settles (`Completed`).
2. Restaurant rejects the order (demo lever `Restaurant:AcceptMode=Reject`).
3. Without compensation, the order is cancelled (or never prepared) while the payment row stays `Completed` — **money stuck**.

`PaymentStatus.Refunded` already existed on the Payment service; no path produced it.

Restaurant is still **in-process** on Day 11 (extracted as its own service on Day 12). The decision is therefore made in Ordering at payment-settled time, not as a separate service consumer yet — same business rule, different process boundary later.

## Decision

1. **Levers**
   - `Restaurant:AcceptMode` = `Auto` (default) | `Reject`
   - `Restaurant:RefundOnReject` = `true` (default) | `false` (break: cancel order, no refund message)
   - `Saga:Mode` = `Choreography` | `Orchestration` (naming/observability only for the compensation sequence)

2. **Happy reject + refund path**
   - On `PaymentCompleted`, if AcceptMode=Reject → cancel order + Outbox `refund-requested` (same transaction when RefundOnReject=true).
   - Payment service consumes `refund-requested`, calls gateway `RefundAsync`, sets status `Refunded`, publishes `payment-refunded`.
   - Ordering consumes `payment-refunded` and surfaces it on the live-tracking bus (SSE).

3. **Idempotency**
   - Inbox on both consumers.
   - Payment unique-per-order + “already Refunded” short-circuit.

4. **Orchestration vs choreography**
   - Default remains event-driven (choreography).
   - `RefundSagaOrchestrator` is the named place that sequences cancel + refund-request; under Orchestration mode it logs explicit steps for teaching/ops clarity. Same DB writes either way.

## Consequences

### Positive
- Students can **prove** why Saga compensation is not optional when money moved first.
- Break lever (`RefundOnReject=false`) shows the stuck-money failure without writing a special branch of domain logic forever.
- Fits existing Outbox/Inbox/Kafka patterns — no new framework.

### Negative / Risks
- Day 11 restaurant decision is in-process; Day 12+ should move the decision into Restaurant.Api consuming `order-confirmed` (revisit). **Done — ADR-062.**
- Gateway refund failure after order cancel still leaves money-stuck until reconciliation (not productized — named for Day 11 honesty).

### Outbox (updated)
Payment stages **both** `payment-results` and `payment-refunded` via `payment.outbox_messages` + `OutboxRelay` (ADR-028), matching Ordering/Restaurant. No direct Kafka publish on the happy charge/refund reply path.

## Alternatives considered
- Only document the refund without implementing it — rejected (ghost lever in runbooks).
- Temporal/MassTransit orchestrator — overkill for one compensation; named in saga-deep-dive only.
- 2PC across Ordering and Payment — rejected (ADR-029).

## References
- ADR-029 (base saga), ADR-028 (Outbox/Inbox), ADR-023 (async payment / reconciliation hole), ADR-062 (Service decision)
- Implementation: `RefundSagaOrchestrator`, `RefundRequestedConsumer`, `PaymentService.RefundAsync`, Payment `OutboxMessage` / `OutboxRelay`, `RestaurantAcceptanceOptions`

## Revisit when
- Restaurant is extracted (Day 12): move AcceptMode into Restaurant.Api on `order-confirmed`. **Done — ADR-062** (`DecisionMode=Service`).
- Refunds can fail at the gateway: model a refund-failed path + reconciliation job.
