# ADR-043: Circuit Breaker + Retry-with-Backoff — Completing the Resilient External-Call Pipeline

**Date:** 2026-06-07
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

We previously wrapped the one call Tadka doesn't own — the payment gateway — in a Polly pipeline with a **timeout + bulkhead** (ADR-021). This ensures a slow gateway can't hold a thread past the deadline, and at most N charges are in flight so a sick gateway can't drain the connection pool. 

However, two failure shapes are still unhandled on that external hop:
1. **Sustained failure.** When the gateway is genuinely down or returning constant errors, Payment keeps *trying* every charge — each one pays the full timeout before failing. We hammer a dependency that's already on the floor, waste our own compute resources, and give the gateway no room to recover.
2. **Transient failure.** A one-off blip (a dropped connection, a momentary 5xx) fails a charge that a single retry a moment later would have settled — meaning we fail orders needlessly.

Note what is **not** a circuit-breaker problem anymore: the Ordering→Payment hop. That uses **Kafka** for async messaging — a down Payment service now just leaves messages waiting on the topic. The circuit breaker belongs where a **synchronous external dependency** actually is: Payment → the payment gateway.

## Decision

Complete the Payment→gateway resilience pipeline by adding, to the existing bulkhead + timeout:

- **Circuit breaker** — failure-ratio **0.5** over a **30 s** sampling window with **minimum throughput 5**, **break for 60 s**, then a half-open probe. While open, charges **fail fast** (no gateway call, no timeout wait). What happens to the order next is the `Payment:OnGatewayUnavailable` lever (fix 2, below): the ratio + minimum-throughput guard means one stray timeout in light traffic does **not** trip it — only a genuinely struggling dependency does.
- **`Payment:OnGatewayUnavailable = Compensate | Buffer`** (fix 2) — what to do with the order when the breaker is open, or the gateway itself times out or is unreachable, as opposed to a business decline (always final). **`Compensate` (default, today's demo)**: save a permanent `Failed` payment row immediately and the order saga compensates (cancels) exactly as a decline does. Fast feedback, but a real bug lived here until this fix: the idempotency check at the top of `ChargeAsync` returns whatever status is already stored for an order, so once `Failed` is committed, a Kafka redelivery just reads it back — the order can never be retried again, even after the gateway recovers. **`Buffer`**: treat "gateway unavailable" as *not attempted* — no Payment row is left behind (see the ghost-row watch-out below), and `PaymentService` throws `GatewayUnavailableRetryLaterException` so `OrderPlacedConsumer` seeks back and redelivers instead of committing, backing off roughly the breaker's own break duration rather than the 300ms poison-retry delay. The order waits and confirms later once the gateway recovers, instead of being cancelled.
- **Retry** — exponential backoff **with jitter**, small max attempts, only on **transient** transport failures (timeout/connection/5xx-equivalent), **never** on a business decline (a declined card is a final answer, not a retry) and never on non-idempotent work without an idempotency guard (we have one: one-payment-per-order unique index, ADR-028/011). Jitter so 500 callers don't retry in lockstep (thundering herd).

**Composition (Polly v8, outer→inner):** bulkhead → retry → circuit breaker → timeout. The bulkhead caps concurrency first; retry handles the transient case; the breaker sits inside the retry so it sees **every attempt** (five failed attempts, about two orders, open it) and short-circuits when the dependency is sick; the per-attempt timeout bounds each try. Retry handles transport failures only, so it never retries a `BrokenCircuitException` (an open circuit fails the call at once) or a decline. The alternative, breaker outside retry, would count whole operations and need five failed *orders* to open.

Circuit-breaker **state transitions are emitted as a low-cardinality metric** through `Tadka.Telemetry` (`OnOpened`/`OnClosed`/`OnHalfOpened`), so the open→half-open→closed cycle is visible on a Grafana panel — the breaker is only useful if operations can *see* it trip and recover.

New `PaymentOptions` configuration parameters expose the breaker's failure-ratio and break-duration with sane defaults.

## Consequences

### Positive
- A down gateway is handled in **milliseconds** (fail-fast) instead of one full timeout per charge; the dependency gets a quiet 60 s to recover.
- Transient blips no longer cost real orders.
- The open/half-open/closed cycle is observable via metrics, meaning resilience is provable, not hoped-for.

### Negative / Risks
- **Over-eager breaker** (no minimum throughput, or too-tight ratio) trips on noise and turns a one-request blip into 60 s of self-inflicted downtime — mitigated by ratio 0.5 + min-throughput 5.
- **Retry without discipline** amplifies load (thundering herd) or double-acts on non-idempotent work — mitigated by jitter, transient-only predicate, and the one-charge-per-order index.
- During the 60 s open window, charges that *would* have succeeded (gateway recovered at 10 s) are refused until the half-open probe — an accepted trade for not hammering a flapping dependency.
- **The ghost-Pending-row trap (fix 2).** `ChargeAsync` writes a `Pending` payment row *before* calling the gateway, and its own idempotency check returns whatever status is already stored for that order. In `Buffer` mode, if that `Pending` row were left in place (or a caught-and-swallowed exception silently returned it), a redelivery would read back `Pending` forever — the order would be stuck for good, not retried, the exact failure Buffer mode exists to avoid. `ChargeAsync` explicitly deletes the row (`db.Payments.Remove` + a fresh `SaveChangesAsync`) before throwing, rather than relying on an ambient transaction to roll it back — it is also called directly over HTTP with no wrapping transaction (`Program.cs`'s `/charge` route), so an implicit rollback isn't always there to catch it.
- **"Payment *service* down" and "the *gateway* Payment depends on down" are different failures, easy to conflate.** A down Payment *service* means the monolith's HTTP call to it throws or times out (ADR-025's cross-service resilience) — Ordering itself must handle that. A down *gateway* (Razorpay-class) is entirely internal to Payment: Kafka still delivers `order-placed`, `PaymentService` still runs, and the breaker/Buffer lever decide what happens next — Ordering never even makes an HTTP call in this path (Day 9 moved payment off the request path onto Kafka). Runbook/day-14 and the script name this split explicitly so "Payment is down" isn't used loosely for both.

### Cost (₹ / effort)
Near-zero infra; a few lines of Polly config + a metric callback. The cost is *judgment* — tuning thresholds per dependency and knowing where a breaker **must not** go (below).

## Alternatives Considered

### Option A: Leave it at timeout + bulkhead (Day-7 state)
- Pros: simplest.
- Cons: every charge against a dead gateway still pays the full timeout; no transient recovery.
- Why rejected: fail-fast + transient recovery are exactly the gaps the brownout day exposed.

### Option B: Retry only (no breaker)
- Pros: handles transient.
- Cons: under a sustained outage, retries *increase* load on the sick dependency — the opposite of help.
- Why rejected: retry without a breaker is a stampede generator.

### Option C: Circuit breaker on *every* call (incl. Postgres, internal reads)
- Pros: uniform.
- Cons: a breaker on a **core, non-degradable** dependency (the DB) adds complexity and helps nothing — you can't serve orders without Postgres. And a breaker on the internal HTTP reads is redundant if they already use a local read replica.
- Why rejected: resilience is **selective** — breakers are for optional/degradable external deps, not core ones (see ADR-044).

## Implementation Notes

- **Topic:** Completing the resilient-external-call pipeline — when retry helps vs harms, what a circuit breaker is, and the three states.
- **Failure mode:** Breaker set to "2 failures, no min-throughput" trips on one network blip and blocks all payments for 60 s with zero real outage; or un-jittered retry turns a 1 s gateway hiccup into a synchronized 3× load spike. Min-throughput + jitter prevent both.
- **Cross-stack equivalents:** Polly (Resilience pipeline) ≈ **Resilience4j** `CircuitBreaker`/`Retry` (Java/Spring) · **opossum** (Node) · **gobreaker** / `failsafe-go` (Go) · or push it to the mesh — **Istio/Envoy outlier-detection + retry policy** (no app code).

## Revisit When
When adding a new external dependency (tune thresholds to its latency/SLO). If the gateway gains a fallback provider, the breaker can route to it instead of failing.

## References
- ADR-021 (Timeout + Bulkhead)
- ADR-011/028 (Idempotency — why retry is safe)
- ADR-044 (Graceful degradation — where breakers must *not* go)
- Implementation: `src/Tadka.Payment.Api/Resilience/PaymentResiliencePipeline.cs`; circuit-state metric via `Tadka.Telemetry`.
