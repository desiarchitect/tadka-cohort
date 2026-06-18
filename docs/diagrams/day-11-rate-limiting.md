# Day 11 — Edge Rate Limiting (diagrams)

YARP gateway applies rate limits at the edge (ADR-035). Token bucket vs leaky bucket — the option-space.

---

## 1. Where limiting sits

```mermaid
flowchart LR
    C([Client flood]) --> GW["YARP Gateway :8080<br/>rate limit HERE"]
    GW -->|allowed| M[Monolith]
    GW -->|allowed| P[Payment]
    GW -->|allowed| D[Delivery]
    GW -.->|429 Too Many Requests| DROP[[fail fast in ms]]
```

Load-shedding at the edge: *a 429 in 50 ms beats a 200 in 30 s* (ADR-044). Gateway is **not** a trust boundary — each service still validates JWT (ADR-031).

---

## 2. Token bucket vs leaky bucket

```mermaid
flowchart TB
    subgraph tb [Token bucket — burst-friendly]
        B1[Bucket holds N tokens] --> R1[Refill rate R tokens/sec]
        REQ1[request] --> T1{token available?}
        T1 -->|yes, consume 1| OK1[allow]
        T1 -->|no| DENY1[429]
    end
    subgraph lb [Leaky bucket — smooth output]
        B2[Queue holds up to N requests] --> LEAK[Drain at fixed rate R req/sec]
        REQ2[request] --> T2{queue not full?}
        T2 -->|enqueue| WAIT[processed at steady R]
        T2 -->|full| DENY2[429]
    end
```

| Algorithm | Burst behaviour | Steady traffic | Typical use |
|-----------|-----------------|----------------|-------------|
| **Token bucket** | Allows short bursts up to bucket size | Average capped at refill rate | API gateways, user-facing APIs (Tadka YARP) |
| **Leaky bucket** | Smooths bursts into steady drip | Strict output rate | Traffic shaping, legacy telco analogies |

> **Interview line:** token bucket = "you get 100 tokens, refill 10/sec — burst 100 then throttle." Leaky bucket = "requests queue and exit at a fixed rate — no burst above the leak rate."

---

## 3. Day-11 topology (3 services + gateway)

```mermaid
flowchart TB
    C[Client] --> G["YARP Gateway :8080<br/>(routing + edge rate-limit)"]
    G -->|/api/v1/orders,restaurants,auth,users| M[Monolith :5224]
    G -->|/api/v1/payments| P[Payment :5240]
    G -->|/api/v1/deliveries| D[Delivery :5250]
    M --> MDB[(ordering DB)]
    P --> PDB[(payment DB)]
    D --> DDB[(delivery DB)]
    D --> R[(Redis-geo)]
    M <-->|Kafka| K{{Kafka}}
    P <-->|Kafka| K
    D <-->|Kafka| K
```