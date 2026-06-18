# Day 9–11 — Saga Choreography (diagrams)

Cross-service consistency without 2PC. See ADR-029 (Saga choreography). Day 11 adds Delivery as the 3rd participant.

---

## 1. Two-participant Saga (Day 9)

```mermaid
sequenceDiagram
    participant O as Ordering
    participant P as Payment
    O->>O: create order (Pending) → emit order-placed
    P->>P: charge → emit payment-results (Completed)
    O->>O: confirm order
    Note over O,P: failure: payment-results=Failed → Ordering COMPENSATES (cancel order)
```

---

## 2. Three-participant Saga (Day 11)

```mermaid
sequenceDiagram
    participant O as Ordering
    participant P as Payment
    participant D as Delivery
    O->>P: order-placed (Outbox→Kafka)
    P->>O: payment-results = Completed
    O->>O: confirm order → order-confirmed (Outbox→Kafka, carries lat/long)
    D->>D: assign rider (idempotent) → delivery-assigned
    Note over O,D: failure: payment Failed → Ordering compensates (cancel)
```

---

## 3. Choreography vs orchestration (option-space)

**Choreography (what Tadka runs)** — no central brain; each service reacts to events.

```mermaid
sequenceDiagram
    participant O as Ordering
    participant P as Payment
    participant D as Delivery
    O->>O: create order → emit order-placed
    P->>P: charge → emit payment-results
    O->>O: confirm → emit order-confirmed
    D->>D: assign rider → emit delivery-assigned
```

**Orchestration (when flow outgrows implicit handlers)** — a coordinator owns commands + compensations.

```mermaid
sequenceDiagram
    participant S as Saga Orchestrator
    participant P as Payment
    participant D as Delivery
    S->>P: command: ChargeOrder
    P-->>S: reply: Charged / Declined
    alt Charged
      S->>D: command: AssignRider
      D-->>S: reply: Assigned
    else Declined
      S->>S: COMPENSATE → CancelOrder
    end
```

| Use **choreography** when… | Use **orchestration** when… |
|---|---|
| 2–3 participants, mostly linear | 4+ participants / complex branching |
| services stay maximally autonomous | you need one place to see + operate the flow |
| flow rarely changes | timeouts, human steps, long-running |

> Rule of thumb: **start choreographed; reach for an orchestrator when you can no longer answer "what happens next?" by reading one file.**

Implementation landscape: manual Kafka handlers (Tadka), MassTransit/NServiceBus (.NET), Temporal, Camunda, Step Functions — pattern is language-neutral.