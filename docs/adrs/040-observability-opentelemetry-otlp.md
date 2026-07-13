# ADR-040: Observability via OpenTelemetry + OTLP — the Three Pillars, Vendor-Neutral

**Date:** 2026-06-06
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Tadka has evolved into a distributed architecture: **4 services + a gateway** stitched by a **choreographed Kafka saga** (order → `order-placed` → Payment → `payment-results` → Ordering confirms → `order-confirmed` → Delivery). The flow we built is now implicit and spread across 5 OS processes plus Kafka. When a customer says *"my order is stuck,"* the on-call has **five separate, uncorrelated log streams** and no way to follow one order across the hops. There are no metrics (is it the whole fleet or one order?), no traces (where did it stop?), and logs are unstructured with no shared id.

This is the classic distributed-systems blindness. We need the **three pillars of observability**:
- **Logs** — *what* happened (one event, one line).
- **Metrics** — *how much / how fast* (aggregate fleet health: rate, errors, latency).
- **Traces** — *where*, across services (one request's path through every hop).

The architectural question is **not** "which dashboard tool" — it is **how to instrument once without marrying a vendor**, because instrumentation code is sticky: rip-and-replace across 5 services is expensive, and the team will want to change backends as the company grows (free OSS now; maybe Datadog/Grafana-Cloud later).

## Decision

Instrument every service with **OpenTelemetry (OTEL)** — the CNCF vendor-neutral standard — and export over **OTLP** to an **OpenTelemetry Collector** that fans telemetry out to the chosen backends. The initial backend stack is deliberately **lean** to minimize infrastructure costs:

- **Traces** → **Jaeger** (own UI, native OTLP ingest).
- **Metrics** → **Prometheus** (Collector exposes a scrape endpoint; Prometheus scrapes the Collector).
- **Dashboards** → **Grafana** (over Prometheus + Jaeger datasources).
- **Logs** → **structured JSON to console** via **Serilog**, every line enriched with `service.name` + `trace_id` + `span_id` (so logs are greppable by trace id and tie back to a trace). **Loki is deferred** until scale demands it.

A shared **`Tadka.Telemetry`** library exposes `AddTadkaTelemetry(serviceName)`: ASP.NET Core + HttpClient + EF Core auto-instrumentation, a custom `ActivitySource`/`Meter`, and the OTLP exporter. The exporter is **gated on `OTEL_EXPORTER_OTLP_ENDPOINT`** — unset means telemetry is off.

The **Collector is the seam**: "instrument once, route anywhere." Swapping Jaeger for Datadog tomorrow is a Collector-config change, **not** an app change.

## Consequences

### Positive
- **One instrumentation, any backend** — no vendor lock-in; the app emits OTLP, the Collector decides where it goes.
- **Cost control** — OSS stack means no recurring license fees while we establish our baseline metrics.
- Auto-instrumentation gives HTTP + DB + runtime telemetry for ~15 lines of config per service; developers learn the *standard*, not a proprietary SDK.
- Graceful degradation: Collector down → exporter drops telemetry, the app is unaffected (fire-and-forget).

### Negative / Risks
- **Operational Burden.** No pre-built dashboards, no vendor support — when Prometheus OOMs at 3 AM, the team owns it (mitigated by ADR-042's cardinality discipline).
- More moving parts in our infrastructure (4 new containers).
- OTEL .NET package churn; broker-aware Kafka auto-instrumentation is young (handled in ADR-041 with manual propagation).

### Cost
OSS backends = **₹0 licence**, only compute costs. A vendor SDK (e.g., Datadog) is **~$15–23/host/month** for APM; at 4 services × 2 instances = 8 hosts ≈ **$120–184/month** just for basic APM — close to the entire small-scale AWS bill. The OTEL path trades that recurring spend for one-time setup + the operational burden of self-hosting.

## Alternatives Considered

### Option A: Datadog (or New Relic) vendor SDK/agent
- Pros: rich auto-instrumentation, beautiful dashboards + APM out of the box, one vendor for all three pillars.
- Cons: **lock-in** (switching backends = re-instrument everything in a proprietary SDK), **$15–23/host/month** (Datadog), data leaves your perimeter.
- Why rejected: at our stage, coupling observability to a vendor — in both code and budget — is the wrong one-way door. OTEL keeps the door open.

### Option B: Full Grafana LGTM stack (Loki + Grafana + Tempo + Mimir/Prometheus)
- Pros: all three pillars in one Grafana UI; the "single pane of glass."
- Cons: ~6 containers (~16 total) → significant infrastructure footprint and memory overhead.
- Why rejected: Jaeger + console-JSON logs deliver the same outcomes at half the footprint. Loki/Tempo will be considered as we scale.

### Option C: Roll our own (Serilog files + a `/metrics` endpoint + grep)
- Pros: zero new infra.
- Cons: no distributed traces (the whole point), no correlation across services, no aggregation.
- Why rejected: it is exactly today's blindness with extra steps.

## Implementation Notes

- **Topic:** Observability as a *design* concern, and instrumenting once without vendor lock-in (OTEL + OTLP + Collector).
- **Choice:** OpenTelemetry → OTLP → Collector → Jaeger (traces) + Prometheus (metrics) + Grafana; Serilog JSON logs correlated by `trace_id`.
- **Failure mode:** Collector or Prometheus falls over → we lose *visibility*, not the order path (exporter is fire-and-forget). The real 2 AM trap is a cardinality blowup OOMing Prometheus — addressed in ADR-042.
- **Cross-stack equivalents:** The pattern is identical everywhere — **Java/Spring**: Micrometer + OTel Java agent; **Node**: `@opentelemetry/sdk-node`; **Go**: `go.opentelemetry.io/otel`.

## Revisit When
Self-hosted observability ops exceed ~10% of engineering time, or the fleet passes ~30 services → move to managed (Grafana Cloud / Datadog) by re-pointing the Collector exporter — no app change needed.

## References
- ADR-041 (trace-context propagation across HTTP + Kafka + Outbox)
- ADR-042 (metrics cardinality limits)
- ADR-027/028 (Kafka + Outbox the trace must traverse)
- Implementation: `src/Tadka.Telemetry`
