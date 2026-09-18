# ADR-021: Resilient External Calls — Timeout + Bulkhead (Polly)

**Date:** 2026-06-04
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Day 7 finally wires payment into the order flow, and payment means an **external gateway** (Razorpay/Stripe-class) — the first hard dependency Tadka does not own. The naive design (which we build first, on purpose) calls the gateway **synchronously, inside the order-creation request**. It works beautifully in the demo: the gateway answers in ~200 ms.

Then we flip the gateway to **slow (8 s)** — exactly what a real payment provider does during its own incident or a network brownout. Now every `POST /orders` holds its request thread **and its Postgres connection** for 8 seconds. At dinner-rush concurrency the connection pool (ADR-015) drains, requests queue, and **order placement p99 collapses — even for orders that don't need the gateway yet.** A slow *payment* dependency has degraded the *whole* monolith. The Day-6 Redis menu cache buys some headroom (browsing still serves from Redis), but the write path dies.

The lesson the outage teaches: **an unbounded, untimed call to something you don't control is a latency bomb wired to every shared resource you do control.**

## Decision

**Wrap every external-gateway call in a Polly v8 `ResiliencePipeline` with two strategies: a timeout and a bulkhead (concurrency limiter).**

- **Timeout (~2 s).** A call that hasn't returned in 2 s is abandoned with a `TimeoutRejectedException`. A slow dependency now fails **fast and contained** instead of holding a thread + connection for 8 s. Fail-fast is the point: a quick, handled failure beats a slow success that takes the system down with it.
- **Bulkhead / concurrency limiter (bounded slots).** Cap the number of *concurrent* in-flight gateway calls (e.g. N permits, small queue). A sick gateway can now consume **at most N** workers — it can never monopolise the whole thread pool / connection pool. Calls beyond the cap are rejected immediately (`429`-class, retryable) rather than piling up.
- **Retry and circuit-breaker are named but deferred to Week 7.** Full resilience — exponential backoff with jitter, a circuit breaker that trips open after sustained failure, load shedding, chaos drills — is its own production topic. Today we install the two strategies that directly answer *this* outage (hold-time and blast-radius); we do not gold-plate ahead of an observed need.

## Consequences

### Positive
- One slow dependency can no longer drain shared pools — the **blast radius is bounded** to N payment slots and ~2 s, not the whole app.
- Fail-fast turns an 8 s hang into a ~2 s, *handled* outcome (payment marked pending/failed, order reacts) — a decision we control, not a timeout the OS eventually forces.
- The pipeline is a reusable seam: every future external call (Day 8 HTTP bridge, third-party SMS, maps) gets wrapped the same way.

### Negative / Risks
- A timed-out payment is now an **outcome you must handle** — retry later, mark pending, or cancel the order. Fail-fast trades a slow success for a fast failure *that has consequences* (see ADR-023's async processor, which owns this).
- The bulkhead **caps payment throughput** by design. Size it wrong (too small) and you reject healthy traffic; too large and it stops protecting. It's a tuned number, revisited with real metrics (Week 7).
- Polly is a new dependency and a new mental model (pipelines/strategies) for the team.

### Cost (₹ / effort)
Zero infra. Polly is a library; the cost is a pipeline definition + tuning two numbers (timeout, permits). The *saving* is not taking a full outage every time a payment provider has a bad five minutes.

## Alternatives Considered
- **No timeout (the naive baseline):** what we deliberately break first. Couples every order to the slowest the gateway will ever be. Rejected — it's the outage.
- **Timeout only, no bulkhead:** stops the 8 s hold per call, but under a burst you can still have hundreds of concurrent 2 s calls saturating threads/connections. The bulkhead caps *how many* can be in flight; the two strategies answer different halves of the problem.
- **Hand-rolled `CancellationTokenSource(timeout)` + a `SemaphoreSlim`:** doable, but Polly composes timeout + bulkhead (+ later retry/breaker) declaratively, with metrics hooks and battle-tested edge cases. Reinventing it is exactly the wheel Polly is.
- **Circuit breaker now:** valuable, but it earns its place once we have failure-rate metrics and a fallback story (Week 7). Adding it today would be ahead of the observed need.

## References
- ADR-015 (connection-pool sizing — the shared resource the brownout drains), ADR-023 (async payment — what handles a fast failure), ADR-013→022 (event seam the payment processor hangs off)
- The brownout lab (slow gateway → pool drain → timeout+bulkhead recovery, with captured numbers) lives in the instructor delivery pack, a separate repo not included in this clone — ask your instructor for it rather than following a path here.
- Implementation: `Infrastructure/Resilience/*` (Polly pipeline), `Domain/Payments/FakePaymentGateway.cs`

## Revisit When
**Week 7 (production resilience):** add retry-with-backoff + a circuit breaker (trip open on sustained gateway failure, fail fast while open), load shedding, and chaos testing — with OpenTelemetry metrics to tune the timeout and bulkhead from real percentiles instead of guesses. Re-tune the bulkhead size whenever instance count or gateway SLA changes.
