# ADR-041: Distributed Trace-Context Propagation across HTTP + Kafka (incl. the Outbox)

**Date:** 2026-06-06
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

ADR-040 instruments each service with OpenTelemetry. But auto-instrumentation alone gives you **four disconnected per-service traces**, not one holistic picture of an order. A **trace** is a tree of spans linked by a shared **trace id** that must be *carried across every process boundary*. Two boundary kinds exist in Tadka:

1. **Synchronous HTTP** (gateway → monolith; service → service queries). Here OTEL's `HttpClient`/ASP.NET Core instrumentation **already** injects + extracts the W3C `traceparent` header automatically. Solved for free.
2. **Asynchronous Kafka** (the saga: `order-placed`, `payment-results`, `order-confirmed`, `menu-updated`). Kafka is **not** auto-propagated by our client — and worse, we publish through the **transactional Outbox** (ADR-028): the HTTP request that wrote the order **has already returned** by the time `OutboxRelay` reads the row and produces to Kafka. The originating span (and its `Activity.Current`) is **long gone**. If we do nothing, Payment's work starts a brand-new root trace, and the saga shatters into unconnected fragments — a stuck order looks like a dead end with no link to the order that caused it.

Making the choreographed saga **visible** as one waterfall trace requires solving this asynchronous propagation gap.

## Decision

Propagate the **W3C Trace Context** (`traceparent`/`tracestate`) explicitly across Kafka, and **persist it through the Outbox** so the async hop rejoins the originating trace:

1. **At enqueue** (inside the order/menu transaction): capture the current `traceparent` from `Activity.Current` and store it in a new **`TraceParent` column** on the outbox row (monolith `ordering` outbox + Restaurant outbox). It commits atomically with the business row — the trace context is as durable as the event itself.
2. **At relay** (`OutboxRelay`): start a short **"publish" span** (child of the stored context), and **inject** the `traceparent` into the **Kafka message headers** via the W3C propagator before producing.
3. **At consume** (every consumer — Payment `OrderPlacedConsumer`, monolith `PaymentResultsConsumer` + `MenuUpdatedConsumer`, Delivery's `order-confirmed` consumer): **extract** the `traceparent` from the message headers and start the processing span **as a child of the extracted context** (or a span *link* when fan-out makes a strict parent wrong). Now Payment's charge span hangs under the same trace as the order span.

A shared **`Tadka.Telemetry`** helper exposes `InjectTraceContext(headers)` / `ExtractTraceContext(headers)` using `System.Diagnostics` + the OTEL `Propagators.DefaultTextMapPropagator`, so every produce/consume seam uses the same code.

We deliberately use **manual W3C propagation via headers + the Outbox column** rather than a broker-auto-instrumentation package: it is robust, version-independent against the young `Confluent.Kafka` OTEL instrumentation, and importantly, it addresses the Outbox time-gap (which a simple auto-instrumentation interceptor would fail to bridge).

## Consequences

### Positive
- **The saga becomes one trace:** gateway → ordering → *(Kafka)* → payment → *(Kafka)* → delivery shows as a single waterfall in Jaeger. Root-causing "where's my order stuck?" becomes a glance.
- Durable context: because `traceparent` rides the Outbox row, even a Payment outage + later catch-up stays on the original trace.
- One shared helper → no per-seam drift.

### Negative / Risks
- **Manual plumbing at every produce/consume seam** — a forgotten inject/extract silently orphans spans (the trace "ends" at that boundary). This is the most common observability bug and a real maintenance tax.
- Async parent-vs-link nuance: a relay that batches N messages shouldn't make all N children of one publish span — use span **links** there (documented in code).
- Tiny payload growth (a `traceparent` header + an outbox column ~55 bytes).

### Cost
Low code cost (one helper + a column + a few call-sites), reusing the Outbox/relay/consumer machinery. The ongoing cost is *vigilance* — every new Kafka topic must carry the header or it falls off the trace.

## Alternatives Considered

### Option A: Do nothing — rely on auto-instrumentation only
- Pros: zero work.
- Cons: HTTP hops link, Kafka hops don't → the saga is still invisible exactly where it matters.
- Why rejected: defeats the day's goal.

### Option B: Broker-auto-instrumentation package (`OpenTelemetry.Instrumentation.ConfluentKafka`)
- Pros: less hand-written code if/when it matures.
- Cons: young/preview against our client + .NET 10; **still cannot see the Outbox** (the producer is the relay, not the request) — you'd inject at the relay anyway, losing the request's context unless you persist it.
- Why rejected: doesn't solve the Outbox gap and adds a fragile dependency; revisit when stable.

### Option C: Carry a hand-rolled `correlationId` field in the message body (not W3C headers)
- Pros: simple, explicit.
- Cons: not a real trace (no spans/timing/parent-child), no tool understands it, reinvents a standard.
- Why rejected: W3C `traceparent` *is* the correlation id and every tool speaks it; a custom field is strictly worse.

## Implementation Notes

- **Topic:** Distributed trace context propagation through async messaging **and a store-and-forward Outbox**.
- **Failure mode:** An engineer adds a new topic and forgets the header → that hop's spans become a separate root → an incident trace "ends" at a healthy-looking service and you chase the wrong box. Guarded with a propagation unit test + a code-review checklist item.
- **Cross-stack equivalents:** Same pattern everywhere — **Spring**: Micrometer Observation + Spring-Kafka's `ObservationRegistry` propagates `traceparent` in record headers (persisted on a JPA outbox row for store-and-forward). **Node**: `@opentelemetry/instrumentation-kafkajs` into `message.headers`.

## Revisit When
A maintained broker-aware OTEL instrumentation for `Confluent.Kafka` is stable on .NET — then keep only the Outbox-context persistence and let the library handle inject/extract on the producer/consumer ends.

## References
- ADR-040 (OTEL/OTLP base)
- ADR-028 (Transactional Outbox/Inbox — the relay we hook)
- ADR-027 (Kafka topics the trace traverses)
- ADR-029 (Choreographed Saga)
- Implementation: `Tadka.Telemetry` Inject/Extract helpers; `TraceParent` outbox column + migrations; consumer span wiring.
