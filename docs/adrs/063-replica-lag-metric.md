# ADR-063: Replica Staleness Metric (`tadka.replica.lag_seconds`)

**Date:** 2026-09-22
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

ADR-037's local read model (`ordering.{restaurant_replica,menu_replica}`, fed by `menu-updated`) keeps
order intake alive when Restaurant is down, at the cost of eventual consistency — but nothing measures
that cost. If the Outbox relay on Restaurant.Api stalls, or `MenuUpdatedConsumer` here falls behind or
dies, orders keep flowing (that's the point of ADR-037) but silently price from an increasingly stale
replica. There was no signal — not a log, not a metric — that would tell anyone this was happening.

Day 13 (observability, OTEL/Jaeger/Prometheus/Grafana) is later in the taught sequence, but this branch
(`day-12`) is intentionally kept fast-forwarded to `main` per `docs/runbooks/DAY-EVOLUTION.md`, and `main`
already carries Day 13's `Tadka.Telemetry` project (`AddTadkaTelemetry`, the shared `Meter`/`ActivitySource`,
OTLP export gated on `OTEL_EXPORTER_OTLP_ENDPOINT`). So the pipeline this metric rides on already exists on
this checkout — this is not "build Day 13 early," it's "use what Day 13 already built here."

## Decision

Emit **`tadka.replica.lag_seconds`** — an `ObservableGauge<double>`, no labels (ADR-042 cardinality
discipline: one series) — measuring seconds since the local menu/price replica last applied a
`menu-updated` event.

- **Baseline, not per-message sample.** `MenuUpdatedConsumer` keeps one static field,
  `TadkaDiagnostics.LastMenuReplicaAppliedEventUnixMs`, updated to the event's own timestamp (Kafka's
  per-message `Timestamp`, not a new field on the wire contract) each time it *actually applies* a
  snapshot. The gauge is evaluated live at scrape time as `now - baseline`. A per-message histogram was
  considered and rejected: if the consumer stalls, no new messages arrive to produce a sample, so a
  histogram would go flat and hide exactly the failure this metric exists to catch. An observed gauge
  keeps counting up from the last known-good point even while nothing is happening.
- **No wire-contract change.** Using Kafka's own message timestamp (`ConsumeResult.Message.Timestamp`)
  as "the event's own timestamp" means `RestaurantSnapshotMessage` didn't need a new field — keeping this
  proportionate to what it is (a lag signal), not a schema change.
- **Reuses the existing pipeline, adds nothing new to run.** One instrument on the `Tadka` meter this
  branch's `Tadka.Telemetry` project already registers (`AddMeter(TadkaDiagnostics.MeterName)` in
  `TelemetryRegistration.cs`). No new exporter, no new collector, no new dashboard config — set
  `OTEL_EXPORTER_OTLP_ENDPOINT` (Day 13's existing gate) and it flows to Prometheus/Grafana like every
  other `tadka.*` metric already does. Unset (tests, single-process dev), it's simply never scraped —
  identical to how every other custom metric in `TadkaDiagnostics` already behaves.

## Consequences

### Positive
- A stalled Outbox relay or a dead/backlogged `MenuUpdatedConsumer` now has a signal: `lag_seconds`
  climbs and keeps climbing, instead of silently serving stale prices forever.
- Zero new infrastructure — this is one static field, one `ObservableGauge`, and roughly ten lines in the
  consumer's consume loop, riding a pipeline this branch already has.
- The lag *calculation* (`TadkaDiagnostics.ComputeReplicaLagSeconds`) is a pure function, unit-tested
  without Kafka, Docker, or a real clock (`tests/Tadka.Api.Tests/Telemetry/ReplicaLagTests.cs`); the
  gauge *wiring* itself is verified in-process with `System.Diagnostics.Metrics.MeterListener`
  (`ReplicaLagGaugeWiringTests.cs`) — no OTLP exporter or Collector needed to prove it reports correctly.

### Negative / Risks
- No alert is wired on this metric yet (Day 14's `tadka.payment.circuit_transitions` got a Grafana panel;
  this one doesn't, yet). A high lag is visible if someone looks, not paged on.
- The gauge tracks *this consumer's* staleness only — it says nothing about whether the Outbox relay on
  the Restaurant side itself is behind (a stalled relay and a stalled consumer look identical from here:
  the replica just isn't updating). Distinguishing them needs the relay to publish its own lag, out of
  scope for this change.
- Kafka's per-message timestamp is `CreateTime` by default (set by the producer's client at send time),
  not a guaranteed monotonic clock across machines — acceptable for a staleness signal at this scale, not
  precise enough for anything stricter.

### Cost (₹ / effort)
No new infra (reuses the Day-13 OTEL pipeline already on this branch). One static field, one instrument,
one call site in an existing consumer, three tests.

## Alternatives Considered
- **Per-message histogram** (`Record((appliedAt - eventTimestamp).TotalSeconds)` on each apply, matching
  `PaymentAmount`'s pattern): rejected — goes flat and hides a stall instead of surfacing it, the opposite
  of what this metric is for.
- **Backport Day 13's full OTEL stack early** (Collector + Prometheus + Grafana dashboards, alert rules):
  rejected as disproportionate — Day 13 hasn't happened yet in the taught sequence, and this branch
  already inherits the pipeline via the cumulative-branch policy, so there was nothing to backport; only
  the one instrument needed building.
- **A dedicated `/health/replica` endpoint returning lag as JSON** (no OTEL involved at all): simpler to
  demo without an observability stack running, but duplicates a signal the metrics pipeline already
  carries once `OTEL_EXPORTER_OTLP_ENDPOINT` is set, and doesn't compose with Grafana. Rejected in favor
  of reusing the one pipeline this system already standardizes on.

## References
- ADR-037 (local read model — the thing this metric watches), ADR-040 (OTEL pipeline this rides on),
  ADR-042 (metrics cardinality discipline — why this is a single unlabeled gauge)
- Implementation: `src/Tadka.Telemetry/TadkaDiagnostics.cs` (the instrument + pure calculator),
  `src/Tadka.Api/Infrastructure/Messaging/MenuUpdatedConsumer.cs` (the update site)
- Tests: `tests/Tadka.Api.Tests/Telemetry/{ReplicaLagTests,ReplicaLagGaugeWiringTests}.cs`
