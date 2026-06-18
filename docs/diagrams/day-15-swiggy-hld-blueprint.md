# Day 15 — Swiggy HLD Blueprint (interview teardown)

Full-ecosystem view for the Design-Swiggy masterclass. Same patterns as Tadka at 1 lakh orders/day; Swiggy at 20 lakh+ with more boxes, not different physics.

---

## 1. End-to-end ecosystem (the boxes interviewers expect)

```mermaid
flowchart TB
    subgraph edge [Edge & client]
        APP([Mobile / Web app])
        CDN([CDN / CloudFront<br/>static assets, menu images])
        APP --> CDN
        APP --> GW
    end

    GW{{API Gateway<br/>Kong ↔ our YARP}}

    subgraph services [Core services — Tadka maps 1:1]
        REST([Restaurant / Catalog<br/>↔ Day 12 + Redis])
        ORD([Order<br/>↔ Ordering Days 3–4])
        PAY([Payment<br/>↔ Day 8/14 + Razorpay])
        DEL([Dispatch / Delivery<br/>↔ Day 11 + Redis-GEO])
        TRACK([Tracking / Notifications<br/>↔ SSE Day 6 + push])
    end

    GW --> REST & ORD & PAY & DEL & TRACK

    subgraph async [Async backbone]
        K{{Kafka<br/>↔ Day 9 Outbox/Saga}}
    end
    ORD --> K
    K --> PAY & DEL & TRACK

    subgraph data [Data plane]
        OLTP1[(Order DB)]
        OLTP2[(Restaurant DB)]
        OLTP3[(Payment DB)]
        OLTP4[(Delivery DB)]
        REDIS([Redis<br/>cache + geo + pub/sub])
        S3([S3 / object store<br/>menu images, receipts])
        ES[(Elasticsearch<br/>search + geo_distance)]
        OLAP[(OLAP / warehouse<br/>Redshift/BigQuery via CDC)]
    end

    ORD --> OLTP1
    REST --> OLTP2 & REDIS & S3
    PAY --> OLTP3
    DEL --> OLTP4 & REDIS
    REST -. index pipeline .-> ES
    OLTP1 -. CDC / Debezium .-> OLAP

    subgraph obs [Observability — Day 13]
        OTEL[OTEL Collector] --> JAE[(Jaeger)] & PROM[(Prometheus)] --> GRAF[Grafana]
    end
    services -. traces/metrics .-> OTEL
```

---

## 2. Tadka ↔ Swiggy mapping (transfer sentence per box)

| Swiggy box | Tadka built | Day / ADR |
|------------|-------------|-----------|
| API Gateway | YARP :8080 | 11 / ADR-035 |
| Restaurant + menu cache | Restaurant service + Redis | 6, 12 / ADR-018, 036–037 |
| Order service | Ordering monolith slice | 3–4 |
| Kafka backbone | Outbox + Saga | 9 / ADR-027–029 |
| Payment + Razorpay | Payment + Polly breaker | 8, 14 / ADR-024, 043 |
| Dispatch + location | Delivery + Redis-GEO | 11 / ADR-033–034 |
| Live tracking | SSE + Redis pub/sub | 6 / ADR-020 |
| Search (ES) | `GET /restaurants` + replica | drill / option-space |
| CDN + S3 | not built in Tadka | drill — static offload |
| OLAP + CDC | not built | drill — analytics path |

---

## 3. Order hot path (labelled arrows)

```mermaid
sequenceDiagram
    participant U as User app
    participant CDN as CDN
    participant GW as Gateway
    participant R as Restaurant
    participant O as Order
    participant K as Kafka
    participant P as Payment
    participant D as Dispatch
    U->>CDN: menu images (cache hit)
    U->>GW: browse / search
    GW->>R: GET menu (cache-aside)
    U->>GW: POST /orders
    GW->>O: create order (price from local replica)
    O->>K: order-placed (Outbox)
    K->>P: charge (idempotent)
    P-->>K: payment-results
    K->>O: confirm
    O->>K: order-confirmed
    K->>D: assign rider (GEOSEARCH)
    D-->>U: push / SSE tracking
```

---

## 4. Contrast anchors (curveballs)

**Zomato search** — `butter chicken near me` → Elasticsearch full-text + `geo_distance` → ML ranker → sponsored slots → A/B → top 20 in &lt;300ms. Tadka's `GET /restaurants` is the seed; ES is the when-to-switch.

**Razorpay** — idempotency key checked first → orchestrator → rails → append-only events → webhooks (at-least-once + HMAC + Inbox dedup). Same correctness primitives as Day 9.

---

## 5. The 45-minute answer skeleton

```mermaid
flowchart LR
  p1[1 Clarify 5'<br/>context + NFRs] --> p2[2 Estimate 5'<br/>scale + cost ₹]
  p2 --> p3[3 HLD 10'<br/>boxes + labelled arrows]
  p3 --> p4[4 Deep dive 15'<br/>options → choice]
  p4 --> p5[5 Failure modes 7'<br/>what breaks at 10x]
  p5 --> p6[6 Wrap 3'<br/>decision + revisit-when]
```

The interview answer **is** the ADR sequence: question → reason → HLD → deep dive → decide.