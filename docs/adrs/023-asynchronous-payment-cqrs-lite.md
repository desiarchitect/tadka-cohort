# ADR-023: Asynchronous Payment — Decouple the Write Path (CQRS-lite)

**Date:** 2026-06-04
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

ADR-021 stops a slow gateway from holding a request for 8 s (it now fails fast at ~2 s). But even a *fast* synchronous payment still asks a question we should challenge: **must placing an order wait for the bank at all?**

Think about what the customer needs from `POST /orders`: a confirmed, priced order with an id — "we got your order." They do **not** need the card to have settled before the screen advances; Swiggy/Zomato show "order placed" instantly and surface payment state moments later. Coupling order creation to payment settlement means every order is hostage to the payment provider's latency and availability — the very coupling the brownout punished. A 2 s fail-fast is better than 8 s, but a *synchronous* call still means: gateway down ⇒ no orders placed.

There's also a correctness angle. Payment is a **side-effect of an order existing**, not a precondition for recording it. Recording the order first, then driving payment, matches the domain and keeps the order as the durable source of truth.

## Decision

**Move payment off the order-creation request. The command creates the order and returns immediately; payment is processed asynchronously and the order status converges.**

- **The write path splits (CQRS-lite).** `POST /orders` does its existing work — validate, server-side price, persist the order (`Created`), record idempotency — then publishes `OrderPlaced` (ADR-022) and **returns 201 right away**. It no longer calls the gateway. This is the *intro* to CQRS: the place-order **command** is separated from the payment write; it is **not** a full read-model/projection split (that's later, if a read need earns it).
- **A background payment processor does the charge.** The Payment module's `INotificationHandler<OrderPlaced>` enqueues the order onto an in-process **`Channel<T>`**; a `BackgroundService` drains it and charges via the **Polly-wrapped gateway** (ADR-021). On success it writes a `Completed` payment row and publishes `PaymentCompleted`; on failure/timeout it writes `Failed` and publishes `PaymentFailed`, and the order reacts (a failed payment **cancels** the order via the existing state machine). The processor is **idempotent** — a unique constraint on `payment(order_id)` means a redelivery can't double-charge.
- **Status converges, and the customer sees it.** The order is briefly "payment pending"; when payment settles, the status change rides the **Day-6 SSE stream** (ADR-020) straight to the customer's screen. SSE is a **notification**, not the payment's source of truth: the row in `payment.payments` is durable and idempotent. If the event is dropped, the screen is briefly behind; `GET /orders/{id}` (or SSE replay) catches up. Never pub/sub for the money itself (ADR-020).
- **The `Payment:Mode` toggle stays in the code** (`Synchronous` | `Async`) purely as the **teaching lever**: `Synchronous` reproduces the brownout for the lab; `Async` is the shipped default. Real systems wouldn't keep both; the cohort keeps it to *show* the before/after.

## Consequences

### Positive
- `POST /orders` is **decoupled from the gateway entirely** — it returns in ms whether the gateway is fast, slow, or down. Orders keep flowing through a payment incident (they settle when it recovers).
- The slow-gateway brownout is structurally impossible on the order path: no request thread or DB connection is ever held waiting on payment.
- Natural retry surface: a failed/timed-out payment is a queue item the processor can re-attempt, with the order as the durable anchor.

### Negative / Risks
- **Eventual payment status.** The order exists before payment settles — the UI must show "payment processing", and downstream logic must not assume paid-on-create. New states/edges to reason about (pending → paid / pending → cancelled-on-failure).
- **The in-memory `Channel` is not durable.** If the process crashes with items queued (or mid-charge), that payment is lost — the order is stuck pending. Acceptable as the *right-sized* step today (single-process, low volume, visible in logs); the **durable** version is **Kafka + the Outbox pattern (Week 5)**, where the event is committed in the same transaction as the order and survives a crash.
- A background worker is now part of the system to operate, observe, and reason about (back-pressure, poison messages) — more than a straight-line request handler.

### Cost (₹ / effort)
Zero infra (in-process `Channel` + `BackgroundService`, no broker yet). Cost is the new states + the operational reality of a worker. The payoff: order intake survives payment-provider incidents, which at 1 lakh+ orders/day is the difference between "payments are slow" and "we can't take orders."

## Alternatives Considered
- **Synchronous payment with just a timeout (ADR-021 alone):** simpler, but the order still can't be placed while the gateway is down, and every order pays the gateway's latency. Fail-fast shrinks the wound; async removes it.
- **Durable queue / Kafka + outbox now:** the correct *destination*, but it's Week 5 — it earns its operational weight when events cross service boundaries and must survive crashes. An in-process channel is the honest intermediate that teaches the decoupling without prematurely buying a broker.
- **Two-phase "reserve then confirm" / payment-first:** heavier protocol, and it re-introduces the coupling (the customer waits on the bank). Wrong trade-off for a food order where the order is the source of truth.
- **Fire-and-forget `Task.Run` instead of a Channel + BackgroundService:** no back-pressure, dies with the request scope, unobservable. The Channel gives a bounded, drainable queue with a single owner — the minimum that's actually operable.

## References
- ADR-021 (timeout + bulkhead around the gateway the processor calls), ADR-022 (the Payment module + `OrderPlaced` event it consumes), ADR-020 (SSE — how the converged status reaches the customer), ADR-011 (idempotency — the no-double-charge principle, here as a unique `order_id`)
- Implementation: `Modules/Payments/PaymentProcessor.cs` (Channel + BackgroundService), `PayForOrderOnOrderPlaced` handler, `PaymentCompleted/PaymentFailed` events
- `cohort-prep/day-07/break-kit-day-07.md` (sync brownout → async recovery), Week 5 (Kafka + Outbox — the durable successor)

## Revisit When
**Week 5:** replace the in-process `Channel` with **Kafka + the Outbox pattern** so the `OrderPlaced` event is committed atomically with the order and survives a crash (at-least-once delivery, consumer idempotency, DLQ for poison payments). At that point the processor becomes a real consumer in (or feeding) the extracted Payment service. Revisit the order state model if product wants explicit "payment failed, retry" UX rather than auto-cancel.
