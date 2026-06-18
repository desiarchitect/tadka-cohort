# Day 14 — Resilience & Dependency Classification (diagrams)

Circuit breaker on Payment→gateway (not Ordering→Payment — that's Kafka since Day 9). See ADR-043, ADR-044.

---

## 1. The reframe — where the cascade actually is

```mermaid
flowchart LR
  subgraph async [Ordering → Payment — SAFE since Day 9]
    ord[Ordering] -->|order-placed| k{{Kafka}}
    k -->|waits if Payment down| pay[Payment]
  end
  subgraph sync [Payment → gateway — REAL synchronous risk]
    pay2[Payment] -->|HTTPS, we do not own it| gw[(External gateway)]
    gw -. down/slow .-> boom[[threads burn, pool drains]]
  end
```

A down Payment = messages **wait**. The breaker belongs on the hop we can't make async: the external gateway.

---

## 2. Circuit-breaker state machine (ADR-043)

```mermaid
stateDiagram-v2
  [*] --> Closed
  Closed --> Open: failure ratio >= 0.5 over 30s AND >= 5 calls
  Open --> HalfOpen: break elapses (60s)
  HalfOpen --> Closed: probe succeeds
  HalfOpen --> Open: probe fails
  note right of Open: fail fast (BrokenCircuitException) — no gateway call
  note right of Closed: min-throughput guard — one blip never trips it
```

State transitions → `tadka.payment.circuit_transitions{state}` on Grafana.

---

## 3. Pipeline onion (outer → inner)

```mermaid
flowchart TB
  b[Bulkhead — max in flight] --> r[Retry — exp backoff + jitter, transport-only]
  r --> cb[Circuit breaker — ratio 0.5 / min 5 / 60s break]
  cb --> t[Timeout — 2s per attempt]
  t --> call([gateway.ChargeAsync])
```

**Declines bypass all of it** — `PaymentDeclinedException` is a final business answer (no retry, no trip). `Outage` lever vs `Failing`=decline.

---

## 4. Dependency classification (ADR-044)

```mermaid
flowchart LR
  subgraph perf [PERFORMANCE deps — degrade gracefully]
    redis[(Redis cache)] -. down .-> db1[(Postgres — slower, still 200)]
    replica[(menu replica)] -. stale .-> ok[orders still priced]
  end
  subgraph corr [CORRECTNESS deps — fail honestly]
    pg[(Postgres)] -. down .-> stop[[no orders]]
    money[payment state] -. NEVER from cache .-> war[[stale paid = cooked]]
  end
```

Plus: **backpressure** = Kafka consumer lag; **load-shedding** = gateway 429 at the edge.