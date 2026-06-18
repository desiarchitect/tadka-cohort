# Day 13 — Observability (diagrams)

Three pillars via OpenTelemetry. See ADR-040 (OTEL stack), ADR-041 (trace propagation), ADR-042 (cardinality limits).

---

## 1. Three pillars — what each answers

```mermaid
flowchart LR
  q[["Order 873b… is stuck — where?"]]
  q --> L[Logs<br/>WHAT happened<br/>Serilog JSON + trace_id]
  q --> M[Metrics<br/>HOW MUCH / HOW FAST<br/>Prometheus RED]
  q --> T[Traces<br/>WHERE across services<br/>Jaeger waterfall]
  L -. grep trace_id .-> ans[Root cause]
  T -. trace ends at Payment .-> ans
  M -. error rate spiked .-> ans
```

---

## 2. OTEL fan-out (instrument once, route anywhere)

```mermaid
flowchart LR
  subgraph svcs [5 entry points — AddTadkaTelemetry]
    gw[Gateway]; mono[Ordering]; pay[Payment]; del[Delivery]; rest[Restaurant]
  end
  svcs -->|OTLP :4317| col[OTEL Collector<br/>vendor-neutral seam]
  col -->|traces| jae[(Jaeger :16686)]
  col -->|metrics :8889| prom[(Prometheus :9090)]
  jae --> graf[Grafana :3000]
  prom --> graf
  svcs -. JSON stdout .-> logs[[console logs<br/>trace_id correlated]]
```

Swap Jaeger → Datadog = change the collector config, not the app. Gated on `OTEL_EXPORTER_OTLP_ENDPOINT` — tests/dev unchanged.

---

## 3. The saga in ONE trace (ADR-041)

```mermaid
flowchart LR
  c([client]) -->|POST /orders| gw[Gateway span]
  gw -->|HTTP traceparent| ord[Ordering: POST api/v1/orders]
  ord --> pub1[outbox publish order-placed<br/>traceparent on the ROW]
  pub1 -->|Kafka header| payc[Payment: consume order-placed]
  payc --> pp[ProcessPayment span]
  pp --> resc[Ordering: consume payment-results]
  resc --> pub2[outbox publish order-confirmed]
  pub2 -->|Kafka header| delc[Delivery: consume order-confirmed]
```

**9 spans, 4 services, one trace.** Without Outbox `TraceParent` + Kafka header injection, this shatters into 4 disconnected traces.

**Wound:** `Payment=Failing` → trace ends at `ProcessPayment` (7 spans); no Delivery span.

---

## 4. Cardinality — ids are NOT metric labels (ADR-042)

```mermaid
flowchart TB
  subgraph bad [order_id as Prometheus label — DON'T]
    o1[order 1] --> s1[series 1]
    o2[order 2] --> s2[series 2]
    o3[1 lakh orders] --> s3[1 lakh series → OOM]
  end
  subgraph good [the fix]
    metric[low-cardinality metric<br/>service · method · route · status]
    span[order_id on SPAN attribute]
    log[order_id in LOG]
  end
```

---

## 5. SLI / SLO / SLA hierarchy

```mermaid
flowchart TB
  sla[SLA — customer promise<br/>99.9% or refunds] --> slo[SLO — internal target<br/>buffer before SLA]
  slo --> sli[SLI — the measurement<br/>successful responses / total]
  slo -. error budget .-> eb[[budget healthy → ship features<br/>budget burned → freeze + fix]]
```

Tadka: Ordering/Restaurant/Delivery 99.9%; **Payment 99.5%** — it rides Razorpay (~99.9%), so 99.99% would be a lie.