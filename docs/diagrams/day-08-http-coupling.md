# Day 8 — The HTTP bridge and the charge that vanishes

**Happy path** — async intake is unchanged; the charge settles over HTTP; status rides Day-6 SSE.

```mermaid
sequenceDiagram
    participant C as Customer
    participant M as Monolith (Ordering)
    participant Q as queue + processor
    participant P as Payment service
    C->>M: POST /orders
    M-->>C: 201 Created (ms — async)
    M->>Q: enqueue OrderPlaced
    Q->>P: POST /payments/charge (HTTP, Polly timeout+bulkhead)
    P-->>Q: 200 Completed (ref)
    Q->>M: publish PaymentCompleted → order Confirmed
    M-->>C: SSE: Confirmed
```

**Payment service DOWN** — this is runbook §2–§3. You get **201**. The charge is **lost**.

```mermaid
sequenceDiagram
    participant C as Customer
    participant M as Monolith (Ordering)
    participant Q as queue + processor
    participant P as Payment service DOWN
    C->>M: POST /orders
    M-->>C: 201 Created (intake still ms)
    M->>Q: enqueue OrderPlaced
    Q->>P: POST /payments/charge (HTTP)
    P--xQ: connection refused / Polly timeout
    Note over Q: charge LOST (queue item consumed) → order stays Created
```

Fault isolation works (monolith is fine). The charge is lost because HTTP needs the callee **now**. Day 9: Outbox + Kafka — a down service means the message **waits**, not vanishes.
