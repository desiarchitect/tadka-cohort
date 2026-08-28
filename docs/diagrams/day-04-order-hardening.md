# Day 4 — Order-Flow Hardening (diagrams)

Three failure-first hardenings of the Day-3 order flow. Each diagram shows the *break* and the *fix*.
See ADR-011 (idempotency), ADR-012 (optimistic concurrency), ADR-013 (in-process domain events).

---

## 1. Idempotency — safe retries on `POST /orders` (ADR-011)

Without a key, a retry creates a duplicate. With a reused `Idempotency-Key`, the replay returns the original order.

```mermaid
sequenceDiagram
    participant C as Client (mobile app)
    participant API as OrdersController
    participant S as IIdempotencyStore
    participant DB as PostgreSQL (ordering)

    Note over C,DB: First attempt — key "abc" is new
    C->>API: POST /orders  (Idempotency-Key: abc)
    API->>S: FindOrderId("abc")
    S->>DB: SELECT … WHERE key = 'abc'
    DB-->>S: (none)
    API->>DB: INSERT order + INSERT key 'abc' (one transaction)
    API-->>C: 201 Created (orderId = O1)

    Note over C,DB: Retry / double-tap — same key "abc"
    C->>API: POST /orders  (Idempotency-Key: abc)
    API->>S: FindOrderId("abc")
    S->>DB: SELECT … WHERE key = 'abc'
    DB-->>S: O1
    API-->>C: 200 OK (orderId = O1) — no duplicate
```

---

## 2. Optimistic concurrency — the lost-update race (ADR-012)

Two writers read the same order version; `xmin` lets exactly one commit. The loser gets `409`.

```mermaid
sequenceDiagram
    participant A as Request A
    participant B as Request B
    participant DB as orders row (xmin = N)

    A->>DB: read order (xmin = N)
    B->>DB: read order (xmin = N)
    A->>DB: UPDATE … WHERE xmin = N
    DB-->>A: 1 row updated → xmin = N+1  (204 No Content)
    B->>DB: UPDATE … WHERE xmin = N
    DB-->>B: 0 rows matched (xmin already N+1)
    Note over B: EF raises DbUpdateConcurrencyException
    B-->>B: middleware → 409 Conflict (reload & retry)
```

> When the two requests *serialise* instead of truly overlapping, the second simply re-reads the
> already-`Confirmed` order and the **state machine** rejects it with `422`. Either way there is no
> lost update: the order is confirmed exactly once.

---

## 3. In-process domain events — dispatch AFTER persistence (ADR-013)

The transition is committed first; side-effects run afterwards and cannot roll it back.

```mermaid
sequenceDiagram
    participant API as OrdersController
    participant O as Order (aggregate)
    participant DB as PostgreSQL
    participant D as IDomainEventDispatcher
    participant H as OrderConfirmedNotificationHandler

    API->>O: Transition(Confirmed)
    O->>O: status = Confirmed; Raise(OrderConfirmedEvent)
    API->>DB: SaveChangesAsync()  (state committed)
    DB-->>API: ok
    API->>D: DispatchAsync(order.DomainEvents)
    D->>H: HandleAsync(OrderConfirmedEvent)
    H-->>D: notification sent (logged)
    Note over API,H: A handler failure here does NOT undo the committed transition.
    Note over D,H: Week 5: dispatcher → Kafka producer, handler → consumer in another service.
```
