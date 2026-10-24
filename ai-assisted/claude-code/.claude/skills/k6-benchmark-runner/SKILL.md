---
name: k6-benchmark-runner
description: Run a k6 load test (smoke, average-load, stress, spike or hot-key) against the Tadka gateway and read the result correctly. Use after an architecture change that could affect latency, throughput or resilience.
allowed-tools: Read Grep Glob
---

# k6 benchmark for Tadka

The profiles live in `k6/` and all go through the gateway on port 8080. The full explanation of each one is in `docs/runbooks/day-16.md`.

## Before you run (these three cause most wrong results)

1. **Raise the rate limits.** All k6 users share one IP, so the default of 120 requests a minute makes a fake "breaking point" (429 errors). Start the gateway with `Gateway__RateLimitPerMinute=100000` and Tadka.Api with `RateLimit__PerMinute=100000`. `scripts/dev-cluster.ps1` sets both for you. A run that sees any 429 fails on purpose.
2. **Bring a token for order writes.** Log in as `priya@tadka.test` (password `Password123!`) and pass `-OrderToken` and `-OrderCustomerId c1b2c3d4-0001-4000-8000-000000000001`. Without them only the browse reads run. The token lives 15 minutes, so log in again before a long run.
3. **Run one test at a time.** Two k6 runs measure each other.

## Run

```powershell
powershell -File ${CLAUDE_SKILL_DIR}/scripts/run-k6.ps1 -Profile smoke
powershell -File ${CLAUDE_SKILL_DIR}/scripts/run-k6.ps1 -Profile average-load -OrderToken $env:TOKEN -OrderCustomerId c1b2c3d4-0001-4000-8000-000000000001
```

## Read the result

- **SLO:** menu and list p99 under 300 ms, under 1% errors. `smoke` and `average-load` assert it. `stress` and `spike` use a relaxed 5 s limit, because there the breach is the finding.
- **A fast API does not mean a healthy system.** `POST /orders` returns 201 before the payment is charged. Look at the **Kafka consumer lag** of the `tadka-payment` group (Kafka UI on port 8090, or the Grafana tile "Orders waiting for payment"). That is where Tadka saturates first.
- `orders_placed` counts 201 responses, not confirmed orders.
- The Payment circuit breaker only opens if you also start a Payment outage (`scripts/inject-incident.ps1 -Scenario payment-outage`). Load alone does not trip it.

Report: profile, users, requests per second, p99, error rate, orders placed, and the queue lag at the end.
