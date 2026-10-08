# Day 13 changelog (since Day 12)

`git checkout day-13`. Previous branch: `day-12`.

## We learned

- The Kafka saga is **invisible** until you add traces. Three pillars: logs, traces, metrics.
- **W3C `traceparent`** across HTTP + Kafka + Outbox `TraceParent` column so the saga is **one trace**.
- Consumer error status: Hoisted activity across consumer try/catch/finally blocks records `ActivityStatusCode.Error` and exception details on failures.
- Metrics: **low-cardinality labels only** (ids on spans/logs, never Prometheus labels).

## Architecture

- `src/Tadka.Telemetry` — `AddTadkaTelemetry(serviceName)`, gated on `OTEL_EXPORTER_OTLP_ENDPOINT`.
- Compose profile `observability` (collector, Jaeger, Prometheus, Grafana). Off by default.

## Code vs Day 12

| Area | What changed |
|---|---|
| `Tadka.Telemetry` | Serilog JSON + OTEL traces/metrics |
| Outbox tables | `TraceParent` column + migration |
| Consumers | Extract header, child span, record error status on exception |
| `tadka.orders.placed` / `payment.result` | Business metrics, created at 0 on start so `increase()` sees the first burst (`PrimePaymentCounters`) |
| Grafana alert | Payment failure alert fires above 3 failures in 5 minutes (was above 0) |
| Tracing filter | `/health`, `/metrics` and `/` are not traced |
| `docker/kafka-scram-entrypoint.sh` | Wipes its own storage on start, so a restarted Kafka container comes back healthy |

ADRs **040, 041, 042**.
