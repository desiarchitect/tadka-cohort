# Restaurant decision mode matrix (ADR-045 / ADR-062)

| | `DecisionMode=Inline` (default) | `DecisionMode=Service` |
|--|--------------------------------|-------------------------|
| **When** | Day 11, tests, single-process | Day 12+ multi-service demo |
| **Who decides** | Ordering at payment-settled | Restaurant.Api on `order-confirmed` |
| **AcceptMode lives on** | Ordering `Restaurant:AcceptMode` | Restaurant.Api `Restaurant:AcceptMode` |
| **Kafka required?** | No | Yes + Restaurant.Api running |
| **If Restaurant down** | N/A | Orders stay **Confirmed**; Delivery may still assign |
| **Refund path** | Same `RefundSagaOrchestrator` | Same, triggered by `restaurant-response` |

## Stuck-Confirmed runbook (Service mode)

If `DecisionMode=Service` and Restaurant.Api is down or not consuming:

1. Orders show `Confirmed` after payment (Ordering did its job).
2. No row in `restaurant.order_decisions`.
3. No `restaurant-response` in Kafka UI.
4. **Fix:** start Restaurant.Api with Kafka; backlog will drain (Inbox-safe).
5. **Demo fallback:** flip Ordering to `Restaurant__DecisionMode=Inline` for the next order.

There is no auto-timeout reject in the teaching build (named as a revisit in ADR-062).
