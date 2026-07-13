# ADR-042: Metrics Cardinality Limits — Low-Cardinality Labels Only

**Date:** 2026-06-06
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

ADR-040 sends metrics to **Prometheus**. Prometheus (like every time-series database) stores **one independent time series per unique combination of metric name + label values**. Each series carries its own in-memory index entry and chunk buffer (~1–3 KB of RAM resident, plus disk). This makes **label cardinality the single most dangerous knob in metrics** — and the trap is seductive, because high-cardinality labels are exactly the ones engineers reach for when debugging ("let me just add `order_id` so I can find this order's latency").

Do that and the math turns hostile fast. Tadka's capacity planning accounts for **1 lakh orders/day**, growing toward lakhs of users. Put `user_id` on a handful of metrics across the 4 services:

> 1,00,000 users × 4 services × ~5 metrics = **20 lakh time series**. At ~3 KB/series that is **~6 GB of RAM just for series metadata** — Prometheus OOMs, restarts, loses data, and now *your monitoring needs monitoring*. The very label you added to debug an incident **causes** the next incident.

This is not hypothetical; it is the most common way self-hosted Prometheus dies. We need a hard organizational rule before anyone ships a metric label.

## Decision

**Prometheus labels are low-cardinality only.** The allowed label set on Tadka metrics is bounded and small:

- `service.name` (4–5 values)
- `http.method` (~5 values)
- `http.route` — the **route template** (`/api/v1/orders/{id}`), **never the raw path** with the id substituted (~20 values)
- `status_code` (~5 values)

Worst-case combination: 5 × 5 × 20 × 5 = **~2,500 series** — trivial. Custom business metrics follow the same rule: `tadka.payment.result{status="success|failed"}` is fine (status is low-cardinality); `tadka.orders.placed` as a plain counter is fine.

**Forbidden as metric labels:** `user_id`, `order_id`, `payment_id`, `session_id`, `email`, raw URL paths, or anything unbounded/per-entity.

**High-cardinality investigations go to the other two pillars:** to ask *"what happened to order X / user Y,"* filter **traces** in Jaeger by the `order.id` **span attribute** (ADR-041 puts ids on spans, where high cardinality is cheap and expected) or **grep logs** by `trace_id`/`order_id` (Serilog JSON, ADR-040). This is the correct separation of concerns: **Prometheus = aggregate fleet health; Jaeger/logs = per-entity forensics.** Mixing them kills Prometheus.

## Consequences

### Positive
- Prometheus stays small and fast (thousands of series, not lakhs) — no OOM, predictable RAM.
- Clear, simple rule any engineer can apply at code-review time ("is this label bounded?").
- Forces ids onto traces/logs, which is where they belong and where they're actually more useful (full context, not just a number).

### Negative / Risks
- **You cannot ask "P95 latency for user X" in Prometheus** — that question must be a Jaeger/log query. Engineers used to "just add a label" must relearn the boundary.
- Requires discipline on every new metric; a single careless label can still slip in and cause an OOM (mitigated by automated CI checks on metric registries where possible, plus code review).

### Cost
Effectively ₹0 to follow; **large cost to violate** — a cardinality blowup requires unplanned RAM (bigger Prometheus instance) plus the incident time to find and migrate the offending label. The cheap path is to never add the label.

## Alternatives Considered

### Option A: Label everything (`user_id`, `order_id` …) for maximum queryability
- Pros: any question answerable in one PromQL query.
- Cons: cardinality explosion → Prometheus OOM/restart loop; the failure scales with success (more users = more series).
- Why rejected: it does not scale past a toy; it is the failure mode itself.

### Option B: Label nothing custom (only auto-instrumented labels)
- Pros: safest.
- Cons: loses useful low-cardinality business signal (e.g. payment success vs failure rate) that *is* safe.
- Why rejected: too conservative — low-cardinality business labels are valuable and safe.

### Option C: Tail-sample/aggregate high-cardinality metrics in the Collector
- Pros: can keep some high-cardinality data downsampled.
- Cons: complexity; still wrong to push per-entity data into a TSDB when traces/logs exist for it.
- Why rejected: over-engineering for Tadka; the pillar separation is simpler and correct.

## Implementation Notes

- **Topic:** Cardinality is the fundamental constraint of time-series metrics. Aggregate metrics vs per-entity traces/logs.
- **Rule:** Allow only `service.name`/`http.method`/`http.route`(template)/`status_code` (+ bounded business labels like `payment.status`). Ban `user_id`/`order_id`/etc. Route per-entity questions to Jaeger attributes / log `trace_id`.
- **Failure mode:** An engineer adds `order_id` to a metric during an incident "to debug faster"; over the next hours Prometheus RAM climbs, it OOM-restarts, and you lose the metrics during the very incident you were debugging. 
- **Cross-stack equivalents:** Identical constraint in every metrics ecosystem — **Spring/Micrometer**: tags blow up the same registry (Micrometer ships a `MeterFilter` cardinality limiter). Datadog/New Relic bill by **custom-metric cardinality**, so the same mistake there is a surprise invoice instead of an OOM.

## Revisit When
**Never** for Prometheus labels — this is a TSDB invariant, not a scale decision you grow out of. (If you genuinely need high-cardinality *metrics*, that requires a different tool like a columnar analytics store, not a Prometheus label.)

## References
- ADR-040 (Prometheus is the metrics backend)
- ADR-041 (IDs live on span attributes — where high cardinality is cheap)
- ADR-032 (PII constraints)
- Implementation: custom `Meter` with bounded labels.
