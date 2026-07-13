# ADR-045: Restaurant Reject + Compensating Refund (Saga completion)

**Date:** 2026-07-13  
**Status:** Accepted  
**Deciders:** Tadka Engineering Team

## Context

Our order process is modelled as a choreographed Saga (ADR-029). A simple failure mode is when a payment declines (`Failed`): we simply cancel the order. Because the payment failed, no money was moved.

However, a more complex failure mode exists:
1. Payment settles (`Completed`).
2. Restaurant rejects the order (e.g. out of stock, closing early).
3. Without a compensating action, the order is cancelled (and food is never prepared) while the payment row stays `Completed` — leaving the customer's **money stuck**.

We need a structured way to issue a refund. `PaymentStatus.Refunded` already exists on the Payment service, but no path previously produced it. Currently, the Restaurant boundaries are structured such that the decision to accept/reject originates in the order flow, triggering a compensating refund to the Payment service.

## Decision

1. **Configuration**
   - `Restaurant:AcceptMode` = `Auto` (default) | `Reject`
   - `Restaurant:RefundOnReject` = `true` (default) | `false` (testing toggle: cancel order, no refund message)
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
   - `RefundSagaOrchestrator` is the named place that sequences cancel + refund-request; under Orchestration mode it logs explicit steps for operations clarity. Same DB writes either way.

## Consequences

### Positive
- Resolves the "stuck money" edge case safely by formally introducing Saga compensation.
- Fits existing Outbox/Inbox/Kafka patterns — no new framework required.

### Negative / Risks
- The accept/reject decision currently lives in Ordering but architecturally belongs in Restaurant. This will be migrated to the Restaurant service consuming `order-confirmed` (see ADR-062).
- Gateway refund failure after order cancel still leaves money stuck until manual reconciliation is performed.

### Outbox (updated)
Payment stages **both** `payment-results` and `payment-refunded` via `payment.outbox_messages` + `OutboxRelay` (ADR-028), matching Ordering/Restaurant. No direct Kafka publish on the happy charge/refund reply path.

## Alternatives considered
- **Do not automate refunds (manual ops only)** — rejected, customer impact is too high for this to be a manual task at scale.
- **Temporal/MassTransit orchestrator** — overkill for one compensation path.
- **2PC across Ordering and Payment** — rejected (ADR-029).

## References
- ADR-029 (base saga), ADR-028 (Outbox/Inbox), ADR-023 (async payment / reconciliation hole), ADR-062 (Service decision)
- Implementation: `RefundSagaOrchestrator`, `RefundRequestedConsumer`, `PaymentService.RefundAsync`, Payment `OutboxMessage` / `OutboxRelay`, `RestaurantAcceptanceOptions`

## Revisit when
- Restaurant business logic is fully extracted: move `AcceptMode` into `Restaurant.Api` responding to `order-confirmed` (addressed in ADR-062).
- Scale requires automating gateway refund failures: implement a refund-failed path + background reconciliation job.
