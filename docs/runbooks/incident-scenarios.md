# Incident scenarios (facilitator answer key)

Students get dashboards only. You inject with `scripts/inject-incident.ps1 -Scenario <name>`.

| Scenario | What you did | Expected symptoms | Recovery |
|----------|--------------|-------------------|----------|
| redis-down | `docker compose stop redis` | Menu slower (DB path); SSE multi-instance broken; flags/rate-limit may degrade | `docker compose start redis` |
| payment-slow | `Payment__Gateway__Behavior=Slow` | Timeouts, bulkhead, maybe circuit OPEN under load | Behavior=Fast; wait half-open |
| kafka-down | `docker compose stop kafka` | Orders 201; payments not settling; outbox backlog | start kafka; watch lag → 0 |
| pool-tight | Max Pool Size=5 + k6 | Latency spike, timeouts on DB | restore pool 50 |
| load-shed | `LoadShed__Enabled=true` | history/invoice 503; place order OK | Enabled=false |
| backpressure | `Backpressure__MaxConcurrent=5` | 429 under concurrent load | MaxConcurrent=0 |

## Capstone

`scripts/incident-replay.ps1` runs redis-down then kafka-down with waits. Pair with Day 13 Grafana + Jaeger.
