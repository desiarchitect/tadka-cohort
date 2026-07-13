# ADR-062: Restaurant Service Accept/Reject (Multi-Service Decision)

**Date:** 2026-07-13  
**Status:** Accepted  
**Deciders:** Tadka Engineering Team

## Context

ADR-045 implemented restaurant reject + refund compensation with the decision **inline** in Ordering at payment-settled time — correct for Day 11 while Restaurant was still in-process.

After Restaurant extraction (ADR-036), production ownership of "will we cook this order?" belongs to **Restaurant.Api**, not Ordering. Keeping AcceptMode only on the monolith couples the teaching lever to the wrong service boundary and skips a real Kafka hop students must see: `order-confirmed` → Restaurant decision → `restaurant-response` → Ordering compensation.

## Decision

1. **Decision modes** (`Restaurant:DecisionMode` on Ordering)
   - `Inline` (default) — Ordering applies `AcceptMode` at payment-settled time (tests / no Restaurant consumer). ADR-045 path.
   - `Service` — Ordering always confirms after payment; Restaurant.Api decides on `order-confirmed`.

2. **Restaurant.Api path** (`Restaurant:AcceptMode` on Restaurant service)
   - Consume `order-confirmed` (Inbox dedup).
   - Persist `order_decisions` audit row (one decision per order).
   - Outbox `restaurant-response` with Status `Accepted` | `Rejected`.

3. **Ordering path**
   - `RestaurantResponseConsumer` consumes `restaurant-response`.
   - On `Rejected` → `RefundSagaOrchestrator` (cancel + optional `refund-requested`).
   - On `Accepted` → no-op (order already Confirmed).

4. **Orchestration persistence** (`Saga:Mode=Orchestration`)
   - In addition to named log steps, write `ordering.saga_instances` rows so operators can query compensation state without grepping logs.

## Consequences

### Positive
- Production multi-service ownership of accept/reject.
- Same compensation machinery as ADR-045; only the trigger moves.
- Tests stay fast: default remains Inline without Kafka/Restaurant.Api.

### Negative / Risks
- Service mode needs Kafka + Restaurant.Api running; misconfigured DecisionMode=Service without the consumer leaves Confirmed orders forever if restaurant never answers.
- Delivery also consumes `order-confirmed` — rejection after assign is a later reconciliation topic (out of scope here).

## Alternatives considered
- Always Service mode — rejected; unit/integration tests would require Kafka + Restaurant.Api.
- HTTP call Ordering → Restaurant for decision — rejected; reintroduces Day-8 temporal coupling on the hot path.

## References
- ADR-045 (inline reject + refund), ADR-028 (Outbox/Inbox), ADR-029/033 (`order-confirmed`), ADR-036 (Restaurant extract)
- Implementation: `OrderConfirmedConsumer` (Restaurant), `RestaurantResponseConsumer` (Ordering), `RestaurantAcceptanceOptions.DecisionMode`, `ordering.saga_instances`

## Revisit when
- Restaurant needs human-in-the-loop SLA timers (auto-reject after N minutes).
- Rejection after rider assignment requires Delivery cancel + customer messaging.
