# ADR-043: Circuit Breaker + Retry-with-Backoff — Completing the Resilient External-Call Pipeline

**Date:** 2026-06-07
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Day 7 wrapped the one call Tadka doesn't own — the payment gateway — in a Polly pipeline with **timeout + bulkhead** (ADR-021): a slow gateway can't hold a thread past the deadline, and at most N charges are in flight so a sick gateway can't drain the pool. That moved Payment into its own service on Day 8 (the pipeline travelled with the dependency it protects, `PaymentResiliencePipeline`).

Two failure shapes are still unhandled on that external hop:
1. **Sustained failure.** When the gateway is genuinely down/erroring (the `Failing` lever), Payment keeps *trying* every charge — each one pays the full timeout before failing. We hammer a dependency that's already on the floor, waste latency, and give it no room to recover.
2. **Transient failure.** A one-off blip (a dropped connection, a momentary 5xx) fails a charge that a single retry a moment later would have settled — so we fail orders we didn't need to.

Note what is **not** a circuit-breaker problem anymore: the Ordering→Payment hop. Day 9 replaced that synchronous HTTP call with **Kafka** — a down Payment now just leaves messages waiting on the topic (the Day-9 catch-up demo), so there's no in-request cascade there to break. The circuit breaker belongs where a **synchronous external dependency** actually is: Payment → the gateway.

## Decision

Complete the Payment→gateway resilience pipeline by adding, to the existing bulkhead + timeout:

- **Circuit breaker** — failure-ratio **0.5** over a **30 s** sampling window with **minimum throughput 5**, **break for 60 s**, then a half-open probe. While open, charges **fail fast** (no gateway call, no timeout wait) and the order saga compensates (cancel) exactly as a decline does. The ratio + minimum-throughput guard means one stray timeout in light traffic does **not** trip it — only a genuinely struggling dependency does.
- **Retry** — exponential backoff **with jitter**, small max attempts, only on **transient** transport failures (timeout/connection/5xx-equivalent), **never** on a business decline (a declined card is a final answer, not a retry) and never on non-idempotent work without an idempotency guard (we have one: one-payment-per-order unique index, ADR-028/011). Jitter so 500 callers don't retry in lockstep (thundering herd).

**Composition (Polly v8, outer→inner):** bulkhead → circuit breaker → retry → timeout. The bulkhead caps concurrency first; the breaker short-circuits when the dependency is sick; retry handles the transient case under the breaker; the per-attempt timeout bounds each try.

Circuit-breaker **state transitions are emitted as a low-cardinality metric** through the Day-13 `Tadka.Telemetry` meter (`OnOpened`/`OnClosed`/`OnHalfOpened`), so the open→half-open→closed cycle is visible on a Grafana panel — the breaker is only useful if you can *see* it trip and recover.

The existing `Payment__Gateway__Behavior` (Fast/Slow/Failing) + `TimeoutSeconds`/`MaxConcurrentCharges` levers drive the demos; new `PaymentOptions` knobs expose the breaker's failure-ratio and break-duration with sane defaults.

## Consequences

### Positive
- A down gateway is handled in **milliseconds** (fail-fast) instead of one full timeout per charge; the dependency gets a quiet 60 s to recover.
- Transient blips no longer cost real orders.
- The open/half-open/closed cycle is observable (Day-13 metric → Grafana), so the resilience is provable, not hoped-for.

### Negative / Risks
- **Over-eager breaker** (no minimum throughput, or too-tight ratio) trips on noise and turns a one-request blip into 60 s of self-inflicted downtime — mitigated by ratio 0.5 + min-throughput 5.
- **Retry without discipline** amplifies load (thundering herd) or double-acts on non-idempotent work — mitigated by jitter, transient-only predicate, and the one-charge-per-order index.
- During the 60 s open window, charges that *would* have succeeded (gateway recovered at 10 s) are refused until the half-open probe — an accepted trade for not hammering a flapping dependency.

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
- Cons: a breaker on a **core, non-degradable** dependency (the DB) adds complexity and helps nothing — you can't serve orders without Postgres. And a breaker on the `RestaurantReadMode=SyncHttp` path is redundant because the default `LocalReplica` already degrades gracefully (ADR-037).
- Why rejected: resilience is **selective** — breakers are for optional/degradable external deps, not core ones (see ADR-044).

## Teaching fields

- **Topic:** completing the resilient-external-call pipeline — when retry helps vs harms, what a circuit breaker is (the MCB/home-fuse), and the three states.
- **Options:** timeout+bulkhead only · retry only · breaker everywhere · **bulkhead+breaker+retry+timeout on the external dep (chosen)**.
- **Choice:** add circuit breaker (ratio 0.5 / min-throughput 5 / 60 s break) + jittered exponential-backoff retry (transient-only) around Payment→gateway; emit breaker state as a metric.
- **Why:** fail fast + give the dep room when it's down; recover the transient case without a herd; make it observable.
- **Trade-off:** a too-tight breaker self-inflicts downtime; the open window refuses some would-succeed calls; retry must be jittered + idempotent-only.
- **Failure mode** (2 AM dinner rush): breaker set to "2 failures, no min-throughput" trips on one network blip and blocks all payments for 60 s with zero real outage; or un-jittered retry turns a 1 s gateway hiccup into a synchronized 3× load spike. Min-throughput + jitter prevent both.
- **Revisit when:** per new external dependency (tune thresholds to its latency/SLO); if the gateway gains a fallback provider, the breaker can route to it instead of failing.
- **Cross-stack equivalents:** Polly (Resilience pipeline) ≈ **Resilience4j** `CircuitBreaker`/`Retry` (Java/Spring) · **opossum** (Node) · **gobreaker** / `failsafe-go` (Go) · or push it to the mesh — **Istio/Envoy outlier-detection + retry policy** (no app code). The pattern (fail-fast on sustained failure, jittered retry on transient) is identical; only the library moves.

## References
- ADR-021 (timeout + bulkhead — the pipeline this completes), ADR-024/025 (Payment extraction; the pipeline moved with it), ADR-027/029 (Kafka + saga — why the Ordering→Payment hop is *not* a breaker site; compensation on a hard decline), ADR-011/028 (idempotency — why retry is safe), ADR-040/042 (the metric is emitted via the Day-13 telemetry), ADR-044 (graceful degradation — where breakers must *not* go).
- `cohort-prep/day-14/option-space.md` (resilience patterns + cross-stack), `break-kit-day-14.md`, `docs/runbooks/day-14.md`.
- Implementation: `src/Tadka.Payment.Api/Resilience/PaymentResiliencePipeline.cs`; circuit-state metric via `Tadka.Telemetry`.
