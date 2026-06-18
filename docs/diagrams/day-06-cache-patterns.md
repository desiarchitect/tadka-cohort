# Day 6 — Cache Patterns (diagrams)

Redis cache-aside on the menu, single-flight stampede protection, and CAP per feature.
See ADR-018 (cache-aside + delete-on-write), ADR-019 (single-flight lock).

---

## 1. Cache miss → populate (with single-flight lock)

```mermaid
sequenceDiagram
    participant C as Client
    participant API as Tadka.Api
    participant R as Redis
    participant DB as Postgres (replica)
    C->>API: GET /restaurants/{id}/menu
    API->>R: GET restaurant:{id}:menu
    R-->>API: (nil) miss
    API->>R: SET lock:{key} NX EX  (single-flight, ADR-019)
    R-->>API: acquired
    API->>DB: SELECT menu
    DB-->>API: rows
    API->>R: SET restaurant:{id}:menu (TTL 60s)
    API->>R: DEL lock:{key}
    API-->>C: 200 menu
```

## 2. Cache hit (the DB never sees it)

```mermaid
sequenceDiagram
    participant C as Client
    participant API as Tadka.Api
    participant R as Redis
    C->>API: GET /restaurants/{id}/menu
    API->>R: GET restaurant:{id}:menu
    R-->>API: {menu}
    API-->>C: 200 menu  (Postgres untouched)
```

## 3. Invalidation — delete-on-write (ADR-018)

```mermaid
sequenceDiagram
    participant C as Restaurant
    participant API as Tadka.Api
    participant DB as Postgres (primary)
    participant R as Redis
    C->>API: PATCH /menu/{item}/availability
    API->>DB: UPDATE (commit)
    API->>R: DEL restaurant:{id}:menu
    API-->>C: 204
    Note over R: next GET misses → repopulates fresh.<br/>TTL is the safety net if this DEL is missed.
```

> **Cache-aside delete, not write-through:** if we *updated* Redis on write and the DB txn then rolled back, the cache would lie. Delete-then-repopulate's worst case is a harmless miss.

---

## 4. Cache-aside vs write-through (option-space)

```mermaid
flowchart TB
    subgraph aside [Cache-aside — Tadka choice ADR-018]
        W1[write path] --> DB1[(DB commit)]
        DB1 --> DEL[DEL cache key]
        R1[read path] --> C1{cache hit?}
        C1 -->|yes| OK1[return cached]
        C1 -->|no| DB1
    end
    subgraph wt [Write-through — NOT used here]
        W2[write path] --> DB2[(DB commit)]
        W2 --> SET[SET cache in same flow]
        SET -. "txn rolls back → cache lies" .-> RISK[[stale/wrong answer]]
    end
```

| Pattern | Write path | Stale risk | Rollback safety | When to switch |
|---------|------------|------------|-----------------|----------------|
| **Cache-aside** | DB first, invalidate cache | TTL-bound stale window | Safe (worst case = miss) | Default for read-heavy, tolerate seconds of staleness |
| **Write-through** | DB + cache updated together | Lower on reads | **Unsafe** if cache update outlives a rolled-back txn | Strong read-after-write on hot keys, or when miss cost is extreme |

---

## 5. Stampede / hot-key (ADR-019)

```mermaid
sequenceDiagram
    participant C1 as 1000 clients (celebrity menu)
    participant API as Tadka.Api
    participant R as Redis
    participant DB as Postgres
    Note over C1,DB: TTL expires on Meghana Foods menu — all requests miss at once
    C1->>API: GET menu (×1000)
    API->>R: GET key → miss
    API->>R: SET lock NX (one winner)
    R-->>API: 1 acquired, 999 wait/retry
    API->>DB: 1 SELECT (not 1000)
    API->>R: SET menu + DEL lock
    API-->>C1: 200 (others hit warm cache)
```

Without the lock: 1000 concurrent misses → 1000 identical DB queries → replica meltdown (Day-14 chaos reuses this lever).

---

## 6. CAP per feature (not per system)

Networks partition; **P is a given**. Per feature: on a partition, choose **Consistency** (error/block) or **Availability** (possibly stale)?

```mermaid
flowchart TD
    Q{On a partition,<br/>what does the feature do?}
    Q -->|"error beats wrong answer"| CP[CP — consistency]
    Q -->|"stale beats unavailable"| AP[AP — availability]

    CP --> P[Payment deduction<br/>double-charge is unacceptable]
    CP --> RYW[Order status the customer just changed<br/>read-your-writes — primary, uncached]
    AP --> M[Menu listing<br/>60s stale is harmless → cached]
    AP --> L[Delivery location<br/>last-known beats unavailable]
```

| Feature | Choice | In Tadka |
|---------|--------|----------|
| Payment | **CP** | never cached |
| Order status (just changed) | **CP / fresh** | primary, read-your-writes (ADR-016) |
| Menu | **AP** | Redis cache-aside, TTL 60s |
| Delivery location | **AP** | SSE + Redis pub/sub (ADR-020) |