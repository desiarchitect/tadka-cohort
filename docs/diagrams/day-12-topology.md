# Day 12 — 4 Services + Gateway (diagrams)

Canonical final architecture. See ADR-036 (extract Restaurant), ADR-037 (event-carried read model), ADR-038 (zero-downtime backfill).

---

## 1. Final topology

```mermaid
flowchart LR
  client([Client]) --> gw[YARP Gateway :8080]
  gw -->|/api/v1/orders,/auth,/users| mono[Monolith :5224\nOrdering + Identity]
  gw -->|/api/v1/payments| pay[Payment :5240]
  gw -->|/api/v1/deliveries| del[Delivery :5250]
  gw -->|/api/v1/restaurants| rest[Restaurant :5260]

  mono --- dbO[(ordering/identity)]
  pay --- dbP[(payment-db)]
  del --- dbD[(delivery-db)]
  del --- geo[(Redis-geo)]
  rest --- dbR[(restaurant-db)]
  rest --- cache[(Redis cache)]

  mono <-->|Kafka| kafka{{Kafka}}
  pay <--> kafka
  del <--> kafka
  rest --> kafka
```

**4 services + gateway** — never "5 microservices."

---

## 2. Read-on-critical-path problem and fix (ADR-037)

```mermaid
flowchart LR
  subgraph before [Naive: sync read on order hot path - the WOUND]
    o1[POST /orders] -->|price?| r1[Restaurant service]
    r1 -. down .-> x1[[order FAILS]]
  end
  subgraph after [Local read model - event-carried state transfer]
    rest2[Restaurant] -->|menu-updated\nOutbox to Kafka| k2{{Kafka}}
    k2 --> c2[MenuUpdatedConsumer] --> rep[(ordering.menu_replica)]
    o2[POST /orders] -->|price from| rep
    rest2 -. down .-> ok2[[order still 201\npriced from replica]]
  end
```

Pricing reads Ordering's **own** replica → order path stays up when Restaurant is down. Trade: seconds-long stale-price window.

`RestaurantReadMode=LocalReplica|SyncHttp` lever shows wound vs fix live.

---

## 3. Zero-downtime backfill (ADR-038)

```mermaid
flowchart LR
  src[(restaurant.menu_items)] -->|"keyset chunk\nFOR UPDATE SKIP LOCKED"| worker[backfill worker]
  worker -->|"idempotent upsert\nON CONFLICT"| dst[(ordering.menu_replica)]
  worker -->|watch replay_lag\nback off if high| lag[[replica lag gauge]]
```

Chunked + throttled + SKIP-LOCKED + lag-watched → table never locks, reads never block. At scale → CDC (Debezium).