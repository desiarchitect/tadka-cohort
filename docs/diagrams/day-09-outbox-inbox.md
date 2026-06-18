# Day 9 — Outbox → Kafka → Inbox (diagrams)

Durable async backbone healing Day-8 temporal coupling. See ADR-027 (Kafka), ADR-028 (Outbox/Inbox).

---

## 1. Happy path — event committed with the order

```mermaid
sequenceDiagram
    participant C as Customer
    participant M as Monolith (Ordering)
    participant DB as ordering DB (order + outbox, one txn)
    participant K as Kafka
    participant P as Payment service
    C->>M: POST /orders
    M->>DB: INSERT order + outbox(order-placed)  [SAME TRANSACTION]
    M-->>C: 201 Created (ms)
    M->>K: OutboxRelay publishes order-placed
    K->>P: consume order-placed
    P->>P: charge (idempotent) + record inbox
    P->>K: publish payment-results (Completed)
    K->>M: consume payment-results (inbox dedup)
    M->>M: Saga → order.Confirm()
    M-->>C: SSE: Confirmed
```

---

## 2. Catch-up — Payment down, message waits

```mermaid
sequenceDiagram
    participant M as Monolith
    participant K as Kafka (retains)
    participant P as Payment ❌ down → ✅ back
    M->>K: order-placed (published, durable)
    Note over K: message sits at offset N — consumer-group LAG = 1
    Note over M: order stays Created (pending), NOT lost
    P->>K: (restart) resume from committed offset
    K->>P: deliver the waiting order-placed
    P->>K: payment-results (Completed)
    Note over K: LAG → 0
    Note over M: order → Confirmed (caught up)
```

---

## 3. What each piece closes

| Piece | Closes |
|-------|--------|
| **Outbox** | dual-write gap — event committed *with* the order |
| **Inbox + unique index** | at-least-once safety — no double charge |
| **Kafka retention** | temporal coupling — work **waits**, never disappears |

Multi-pod relay: `FOR UPDATE SKIP LOCKED` on OutboxRelay (ADR-028) — one row claimed by one pod.