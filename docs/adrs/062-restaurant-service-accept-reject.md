# ADR-062: Restaurant Service Accept/Reject (Multi-Service Decision)

**Date:** 2026-07-13  
**Status:** Accepted  
**Deciders:** Tadka Engineering Team

## Context

ADR-045 implemented restaurant reject + refund compensation with the decision **inline** in Ordering at payment-settled time.

After Restaurant extraction (ADR-036), production ownership of "will we cook this order?" belongs to **Restaurant.Api**, not Ordering. Keeping AcceptMode only on the monolith couples the acceptance logic to the wrong service boundary and skips a critical asynchronous decision flow: `order-confirmed` → Restaurant decision → `restaurant-response` → Ordering compensation.

## Decision

1. **Decision modes** (`Restaurant:DecisionMode` on Ordering)
   - `Inline` (legacy/fallback) — Ordering applies `AcceptMode` at payment-settled time. (ADR-045 path).
   - `Service` (production) — Ordering always confirms after payment; Restaurant.Api decides asynchronously on `order-confirmed`.

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
- True multi-service ownership of accept/reject, aligning with domain boundaries.
- Reuses the existing compensation machinery from ADR-045; only the trigger point moves.

### Negative / Risks
- Service mode relies on Kafka messaging and Restaurant.Api availability; a misconfiguration or outage where the restaurant never answers could leave orders in a Confirmed state indefinitely.
- Delivery also consumes `order-confirmed` — rejection after rider assignment is a later reconciliation topic (out of scope here).

## Alternatives considered
- Always enforce Service mode — rejected; maintaining the Inline mode facilitates simpler integration testing environments where full infrastructure isn't required.
- Synchronous HTTP call Ordering → Restaurant for decision — rejected; reintroduces temporal coupling on the hot path.

## References
- ADR-045 (inline reject + refund), ADR-028 (Outbox/Inbox), ADR-029/033 (`order-confirmed`), ADR-036 (Restaurant extract)
- Implementation: `OrderConfirmedConsumer` (Restaurant), `RestaurantResponseConsumer` (Ordering), `RestaurantAcceptanceOptions.DecisionMode`, `ordering.saga_instances`
