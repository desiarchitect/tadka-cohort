# Runbook — Day 13: Observability for the 4-service system

**What changed since Day 12:** [`docs/changelog.md`](../changelog.md).

The choreographed Kafka saga (order → payment → delivery) is now **visible**. Three pillars, instrumented once with **OpenTelemetry** (ADR-040), exported to a lean OSS stack — **Collector → Jaeger (traces) + Prometheus (metrics) + Grafana (dashboards)**; logs are structured JSON to console correlated by `trace_id`. Trace context crosses **Kafka + the Outbox** (ADR-041); metrics stay **low-cardinality** (ADR-042).

## Start the stack
```bash
git checkout day-13
docker compose --profile observability up -d         # core + otel-collector, jaeger, prometheus, grafana
```
```powershell
# Telemetry is GATED on this env var (unset = off, tests/dev unchanged). Set it before each service:
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
dotnet run --project src/Tadka.Restaurant.Api   # :5260
dotnet run --project src/Tadka.Payment.Api      # :5240
dotnet run --project src/Tadka.Delivery.Api     # :5250
dotnet run --project src/Tadka.Api              # :5224
dotnet run --project src/Tadka.Gateway          # :8080
```
UIs: **Jaeger** http://localhost:16686 · **Prometheus** http://localhost:9090 · **Grafana** http://localhost:3000 (admin/admin). Seeded pwd `Password123!`.

## Demo 1 — The saga in one trace
```bash
# login priya@tadka.test, POST /orders via :8080 (2x Chicken Biryani at Meghana). Then open Jaeger.
#   -> ONE trace, ~9 spans, 4 services:
#      gateway -> ordering -> [outbox publish order-placed] -> Kafka -> payment (consume + ProcessPayment)
#      -> Kafka -> ordering (consume payment-results) -> [outbox publish order-confirmed] -> Kafka -> delivery
# WOUND: restart Payment with Payment__Gateway__Behavior=Failing -> the failed order's trace ENDS at Payment
#        (payment.status=failed, no Delivery span). Root cause in one glance.
```

## Demo 2 — RED dashboard + cardinality blow-up
```bash
# Grafana -> "Tadka - System Overview (RED + business)": request rate / error rate / P95 per service,
#   orders/min, payment results by status. (Metrics export every ~60s — give it a cycle.)
# CARDINALITY: relaunch the monolith with OTEL_CARDINALITY_DEMO=true, place a few orders ->
#   tadka_orders_placed_by_id_total gains ONE series per order_id; panel 6 (active series) climbs.
#   At 1 lakh orders/day = 1 lakh new series/day -> Prometheus OOM. Revert the flag = fixed (ADR-042).
```

## Demo 3 — Logs by trace_id
```bash
# Every service logs JSON with trace_id/span_id/service.name. Copy a trace_id from Jaeger,
# grep it across all 5 services' console output -> the full cross-service story in one filter.
```

## Demo 4 — SLO alert
```bash
# Honest SLOs (Payment 99.5% - it rides Razorpay). The provisioned alert "Payment failure rate above SLO"
# fires on the BUSINESS signal (a decline is a 200-with-Failed, not a 5xx).
# Trip: Payment__Gateway__Behavior=Failing + a burst of orders -> Grafana Alerting: pending -> Firing.
# Restore (Fast) -> clears.
```

## What changed
- NEW `src/Tadka.Telemetry` — `AddTadkaTelemetry(serviceName)` (Serilog JSON + OTEL traces/metrics, gated on the OTLP env var); `TadkaTrace` (W3C inject/parse); `TadkaDiagnostics` (shared `ActivitySource` + `Meter`). Referenced by all 5 entry points.
- Trace propagation: `TraceParent` column on both outbox tables (+ migration); relays inject + open a publish span; all consumers extract + child-span; Payment/Delivery direct producers inject from the ambient span.
- Consumer error status: Activity is hoisted across the consume try/catch/finally blocks; caught exceptions mark the span as `ActivityStatusCode.Error` and record the exception before disposing in `finally` (Fix 6).
- Custom: `ProcessPayment` span; `tadka.orders.placed` / `tadka.payment.result{status}` / `tadka.payment.amount` metrics. `OTEL_CARDINALITY_DEMO` lever (anti-pattern, off by default).
- `docker-compose.yml`: `otel-collector`, `jaeger`, `prometheus`, `grafana` under `profiles: ["observability"]`; configs under `docker/observability/`.

## Ports (Day 13 additions)
otlp-grpc :4317 · otlp-http :4318 · collector-prom :8889 · jaeger-ui :16686 · prometheus :9090 · grafana :3000 · **PgBouncer :6432** (Day 11, carried forward — not a Day-13 teaching beat) (+ Day 1–12 ports unchanged)

Windows: `curl.exe`. Set `$env:OTEL_EXPORTER_OTLP_ENDPOINT="http://localhost:4317"` **before each** `dotnet run`.

## Reset
`docker compose --profile observability down` (keeps DB volumes) · `down -v` (wipes all). Unset `OTEL_EXPORTER_OTLP_ENDPOINT` to run telemetry-off.

## Tests
`dotnet test Tadka.slnx` → **183 tests** passing across all services and integration suites (Api, Payment, Delivery, Restaurant, Gateway). Telemetry is off in tests (no OTLP endpoint), so behaviour is unchanged.
