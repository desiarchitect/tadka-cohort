# Day 8 — Before / After: one process → two services + two databases

Read this next to the runbook [`day-08.md`](../runbooks/day-08.md).

**Before (Day 7 — modular monolith):** Payment is a clean *module*, but it shares the monolith's **process** and **database**. A payment fatal or a payment-DB outage takes the whole host down.

```mermaid
flowchart TB
    subgraph M["Tadka.Api — ONE process"]
        O["Ordering"] -- "OrderPlaced (in-proc MediatR)" --> P["Payment module<br/>gateway + PaymentService"]
        P -- "PaymentCompleted/Failed" --> O
    end
    M --> DB[("PostgreSQL<br/>ordering + payment schemas<br/>(shared fate)")]
```

**After (Day 8 — first extraction):** Payment is its own **service** with its own **database**, reached over **HTTP**. Kill Payment (or its DB) and the monolith keeps serving menus.

```mermaid
flowchart TB
    subgraph MONO["Tadka.Api (monolith)"]
        O["Ordering"] -- "OrderPlaced" --> Q["queue + PaymentProcessor"]
        Q -- "IPaymentClient (HTTP + reused Polly)" --> NET(("HTTP"))
        EV["PaymentCompleted/Failed → Ordering reacts"] --> O
    end
    subgraph PAY["Tadka.Payment.Api (NEW service)"]
        EP["POST /payments/charge<br/>GET /payments/{orderId}"] --> GW["gateway + PaymentService"]
    end
    NET --> EP
    EP -. "result" .-> EV
    MONO --> CDB[("tadka DB<br/>ordering only")]
    PAY --> PDB[("tadka_payment DB<br/>payment only")]
```

The two boxes share **no code** and **no database** — only the event/HTTP **contract**. That contract becomes a Kafka topic on Day 9.
