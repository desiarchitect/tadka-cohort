# ADR-029: Saga (Choreography) for the Order↔Payment Distributed Transaction

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

In our initial monolithic architecture, the "create order + charge payment" flow was executed as a single local database transaction. If the payment failed, the entire operation rolled back atomically. Following the extraction of the Payment service into its own process and database (ADR-024/026), there is no longer a transaction boundary spanning both domains. This introduces the risk of inconsistent states (e.g., an order created but the payment fails, or vice versa). Two-phase commit (2PC) is not a viable solution due to its performance overhead, resource locking, and poor scalability across independently deployed services.

## Decision

We will model the order↔payment flow as a Saga using the **choreography** pattern (without a central orchestrator).

1. Order is created in a `pending` state. The Ordering service emits an `order-placed` domain event via the Outbox pattern (ADR-028).
2. The Payment service consumes this event, executes its local transaction (attempting the charge and persisting the result), and emits a `payment-results` event (`Completed` or `Failed`).
3. The Ordering service consumes the result event and executes a local transaction: 
   - `Completed` → transitions the order to a confirmed state.
   - `Failed` → triggers a compensating action to cancel the order.

Each step operates as a local ACID transaction. Cross-service consistency is achieved eventually via events and compensations. We choose choreography over orchestration because the current flow involves only two participants and is strictly linear. An orchestrator would introduce unnecessary complexity at this stage.

## Consequences

### Positive
- **No Distributed Locks:** Services commit independently without 2PC, maintaining high availability and autonomy.
- **Explicit Compensations:** The "cancel on failure" logic maps naturally to the existing order state machine.
- **Resilience:** The approach is inherently durable and idempotent, leveraging the existing Outbox/Inbox infrastructure.

### Negative / Risks
- **Eventual Consistency Window:** Orders exist briefly in a `pending` state before resolving. This state must be accurately reflected in client-facing UIs.
- **Implicit Flow:** Choreography scatters the business process across multiple event handlers. As more participants join, tracing the end-to-end flow becomes difficult.
- **Complex Compensations:** Business logic for compensations (e.g., refunding a charge if a subsequent step fails) must be carefully designed to handle edge cases like timeouts.

### Cost
Implementation is limited to application code (event handlers and state machine updates). The architectural payoff is robust cross-service consistency without the latency penalties of distributed transactions.

## Alternatives Considered
- **Two-Phase Commit (2PC):** Rejected due to blocking behavior, latency, and incompatibility with autonomous microservices.
- **Saga Orchestration:** Introducing a central coordinator (e.g., Temporal, MassTransit Saga State Machine) was rejected as overkill for a two-step linear process. This will be revisited if the workflow expands.
- **Ignore Partial Failures:** Relying on simple retries without compensation leaves inconsistent data on failure. Rejected.

## Revisit When
We will evaluate transitioning to Saga Orchestration when a third or fourth participant (e.g., Delivery, Restaurant integration) joins the flow, making the implicit choreography too difficult to trace, monitor, or maintain.
