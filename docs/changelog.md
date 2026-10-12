# Day 16 changelog (since Day 15)

`git checkout day-16`. Previous branch: `day-15`.

## We learned

- Load testing verifies architectural hypotheses: smoke, average load, stress (finding the knee), spike (recovery), and hot-key (cache stampede).
- Local loopback trap: virtual users sharing 127.0.0.1 trip the 120 req/min edge rate limiter. True capacity testing requires elevating rate limit thresholds.
- Unit economics: infrastructure cost per 1,000 orders is an architectural decision. Monolith baseline (₹2.03) vs 4-service distributed baseline at 1 lakh/day (₹10.43) vs 10 lakh/day (₹2.93).
- Live cloud deployment with Azure Container Apps (ADR-064): automated provisioning, private PostgreSQL networking, CDN caching, and rapid teardown.
- Capstone portfolio: connecting architecture records, empirical load metrics, and cost economics into a cohesive defense.

## Architecture

- Production capstone milestone: full empirical measurement and cloud verification.
- System operates the canonical 4 services + gateway with full persistence, messaging, observability, and resilience.
- 16 Kafka topics (8 primary + 8 dead-letter queues) with SASL/SCRAM authentication.
- k6 load test suite hardened against false rate-limiting knees.

## Code vs Day 15

| Area | What changed |
|---|---|
| Load Testing | Hardened `k6/lib.js`, `k6/average-load.js`, `k6/stress.js`, `k6/spike.js`, `k6/hot-key.js`, and `k6/README.md` |
| Application code | None (zero code changes; test harness and measurement milestone) |
| Documentation | New student runbook `docs/runbooks/day-16.md`, updated `docs/architecture.md`, `docs/cost-model.md`, `README.md`, and `docs/runbooks/README.md` |
| ADR Canon | 66 ADR files, numbered 001-067 (no 046) |
| Tests | Unchanged from Day 15 (5 test projects) |

ADRs through **067** (046 unused).
