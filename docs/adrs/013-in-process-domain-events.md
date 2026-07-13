# ADR-013: In-Process Domain Events (synchronous now, message bus at extraction)

**Date:** 2026-06-02
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

When an order is confirmed, *things should happen*: notify the customer, (later) capture payment, (later) start looking for a delivery partner. The naive approach is to do all of that **inside** `Order.Transition()` or the controller, right after the status changes. That couples the state change to its side-effects in two harmful ways:

1. **A side-effect failure corrupts the core operation.** If sending the SMS throws, do we roll back the confirmation? The order *is* confirmed — the customer's food should be cooked regardless of whether one notification went out. Coupling makes a trivial failure (notification) able to undo a critical one (the order).
2. **The aggregate accretes dependencies.** `Order` would need a notification client, a payment client, a delivery client — it would know about half the system. That is the opposite of the clean module boundaries we are protecting for extraction (ADR-003, ADR-008).

We need a way for the order to *announce* "I was confirmed" and let interested parties react **independently**, without the order knowing who they are. And we want the mechanism to be an in-process foundation that seamlessly transitions to cross-service messaging during future service extraction.

## Decision

**Raise in-process domain events from the aggregate; dispatch them synchronously, in-process, AFTER the state is persisted.**

- `Order` records events (`OrderPlaced`, `OrderConfirmed`) in an in-memory list as part of its behaviour. It depends on nothing external.
- The controller calls `IDomainEventDispatcher.DispatchAsync(order.DomainEvents)` **after** `SaveChangesAsync()` succeeds, then clears them. Persistence first, side-effects second — a failed handler can never roll back a committed transition.
- A handler (`IDomainEventHandler<TEvent>`) subscribes to an event. Many handlers can subscribe to one event (fan-out). Today there is one: `OrderConfirmedNotificationHandler`, which logs a "notification sent" line.
- Dispatch is **synchronous and in-process** — a method call, no broker, no network, no new infrastructure.

The shapes are chosen deliberately: an **event** (past tense, "something happened") and **independent handlers** are exactly an *event + its consumers* on a message bus. At extraction the dispatcher is swapped for a broker producer (Kafka) and the handlers become consumers in other services — **the domain code does not change**.

## Consequences

### Positive
- **Side-effects are decoupled from the transition.** A notification failure does not undo a confirmed order.
- **The aggregate stays clean.** `Order` knows about *events*, not about notification/payment/delivery clients. Module boundaries stay sharp for extraction.
- **Fan-out for free.** Adding a second reaction (e.g. analytics) is a new handler, zero changes to the order flow.
- **Seamless extraction path.** The team builds the in-process version now, allowing for a straightforward transition where only the transport changes during service extraction.

### Negative
- **Synchronous dispatch is still in the request path.** A slow handler slows the response. Acceptable now (handlers are trivial/local); the fix (async/outbox) is a later step.
- **"After SaveChanges" is at-most-once.** If the process crashes between commit and dispatch, the event is lost — there is no delivery guarantee yet.

### Risks
- **The crash window.** Commit succeeds, dispatch never runs → a missed notification. **Mitigation (future):** the **transactional outbox** pattern — persist events in the same transaction, dispatch from the outbox with retries. Deliberately deferred so the *need* for it is proven, not assumed.
- **Handlers doing too much.** A handler that does heavy/remote work blocks the response. **Mitigation:** keep handlers thin now; move to background/async when a handler grows.

### Cost (₹ / effort)
Zero infrastructure — a dispatcher, an interface, and a handler, all in-process. The cost is a little indirection (an event + a handler instead of an inline call) in exchange for clean boundaries and a painless extraction later. The *deferred* costs (outbox, broker) are paid only when reliability or decoupling actually demands them.

## Alternatives Considered

### Option A: Inline side-effects in the controller / `Transition()`
- Pros: simplest to read; no indirection.
- Cons: couples critical and trivial operations; bloats the aggregate with clients; rewrites the call sites at extraction.
- Why rejected: it builds in exactly the coupling we will pay to remove during extraction.

### Option B: A message broker now (Kafka/RabbitMQ)
- Pros: real delivery guarantees; the "final" architecture immediately.
- Cons: a broker cluster to run, monitor, and reason about (eventual consistency, idempotent consumers, consumer lag) — for a single-process app with ~1.2 orders/s.
- Why rejected: Premature optimization. The broker is justified only when there are actual separate services to decouple; until then it adds cost without benefit.

### Option C: MediatR (in-process mediator library)
- Pros: batteries-included notifications/handlers; popular.
- Cons: a dependency and a programming model to adopt for what a ~30-line hand-rolled dispatcher does transparently.
- Why rejected: for one event and one handler, a tiny explicit dispatcher is cleaner and dependency-free. Revisit if the in-process event surface grows large.

## References
- ADR-002: monolith-first · ADR-003: schema-per-domain · ADR-008: no cross-schema FKs
- `Domain/Common/IDomainEvent.cs`, `IDomainEventHandler.cs`, `DomainEventDispatcher.cs`
- `Domain/Orders/Events/*`, `Domain/Orders/Events/Handlers/OrderConfirmedNotificationHandler.cs`

## Revisit When
At **service extraction**: swap the in-process dispatcher for a **broker producer** and turn handlers into **independent consumers**. Before that, if the **crash window** matters for a critical reaction, introduce the **transactional outbox** so events survive a crash and are delivered at-least-once.
