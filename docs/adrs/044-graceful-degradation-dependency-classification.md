# ADR-044: Graceful Degradation & Dependency Classification

**Date:** 2026-06-07
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

A circuit breaker (ADR-043) handles a *down external dependency* on the request path. But Tadka has several dependencies with very different failure consequences, and "add a circuit breaker" is the wrong reflex for most of them. We need a **policy**: for each dependency, what happens when it's down for five minutes, and is degrading even *safe*?

Consider these production scenarios:
- **Redis down.** The menu cache (ADR-018) is gone. Should orders fail? No — cache-aside is designed to **fall through to Postgres**: correct answers, just slower (DB read-load + p95 rise). Redis is a **performance** dependency, not a **correctness** one. The danger is the *opposite* reflex — making Redis HA and treating it as a hard dependency turns a performance aid into a new failure domain.
- **Hot key.** One celebrity-restaurant menu key expires and the herd stampedes Postgres at the TTL boundary. The single-flight lock (ADR-019) collapses the herd to ~1 refresh. Degradation here is about *not amplifying* load.
- **Postgres down.** There is no graceful degradation — you cannot accept an order without the order store. A circuit breaker here adds complexity and saves nothing. This is a **correctness** dependency.
- **Money data.** The most dangerous degradation of all: serving a **stale "payment successful"** from a cache/fallback when the charge actually failed. Restaurants cook food for orders that were never paid. Some degradations are forbidden.

## Decision

**Classify every dependency and write its degradation rule.** Two classes:

- **Performance dependency** (Redis cache, the local read replica ADR-037): on failure, **degrade** — fall through to the source of truth (DB), accept higher latency / load, never error the user. Size the source of truth to survive a full outage of the performance layer ("the DB must survive Redis being gone"). The hot-key case is bounded by the single-flight lock (ADR-019).
- **Correctness dependency** (Postgres, the order/payment store): on failure, **fail honestly** — do not fabricate or serve stale state. No circuit breaker (it can't help), no stale fallback.

**Rules:**
1. **Per-feature degradation, not all-or-nothing.** Payment down → browsing, menus, order history still work (Kafka makes the order itself wait, ADR-027); only the paid step is affected. A dependency failure degrades *its* feature, not the whole app.
2. **Never degrade money to stale.** Financial state is never served from cache/fallback as if fresh; a wrong "paid" is worse than an honest "try again."
3. **Protect yourself, not just downstream.** Beyond breakers (downstream protection), use the existing **backpressure** signal (Kafka consumer lag — pause/scale, don't silently fall behind) and **edge load-shedding** (the YARP gateway's rate-limit/429, ADR-035 — *a 503 in 50 ms beats a 200 in 30 s*; shed at the edge, not half-way through a request chain).
4. **Every dependency carries a one-line degradation rule** in the owning service's docs; a new dependency without an answer to "what happens if this is down 5 minutes?" isn't done.

## Consequences

### Positive
- A clear, simple decision matrix per dependency — no reflexive "breaker on everything."
- Redis can stay a **simple, non-HA** performance aid (cheaper, fewer failure modes) because the system is proven to survive its loss.
- Forbidding stale-money degradation prevents the worst class of incident (paying-for-unpaid-orders).

### Negative / Risks
- Requires sizing Postgres for the **cache-cold** read load (or accepting a measured p95 hit during a Redis outage) — a real capacity decision.
- Per-feature degradation needs each feature to declare its rule; an unclassified dependency is a latent surprise.

### Cost (₹ / effort)
Mostly design + documentation cost. The infra cost is the choice it *avoids*: you don't have to run Redis HA (a saving) once you've proven the DB survives its loss — or you consciously pay for HA if the p95 hit is unacceptable. That trade is now explicit, not accidental.

## Alternatives Considered

### Option A: Make every dependency HA / hard (Redis cluster, etc.)
- Pros: fewer fall-through events.
- Cons: every dependency becomes a new failure domain + cost; you've turned a performance aid into a correctness one.
- Why rejected: contradicts "cache is disposable"; pay for HA only when the degraded p95 is genuinely unacceptable.

### Option B: Fail completely when any dependency is down
- Pros: simple, no stale-data risk.
- Cons: blocks browsing/menus/history because payments are down — needless.
- Why rejected: per-feature degradation is strictly better for everything *except* money (where we do fail honestly).

### Option C: Cache everything and serve stale as fallback (max availability)
- Pros: highest uptime numbers.
- Cons: stale **money** state = real financial harm.
- Why rejected: availability theatre that causes paying-for-unpaid incidents.

## Implementation Notes

- **Topic:** Resilience is a *policy*, not a pattern — classify dependencies (performance vs correctness) and decide degradation per feature; protect yourself with backpressure + load-shedding.
- **Rule:** Redis/replica = performance (fall through to DB); Postgres/money = correctness (fail honestly, no breaker, no stale); shed at the edge; backpressure on Kafka lag.
- **Failure mode:** A team made Redis a hard dependency "for speed" — Redis hiccuped and took the whole menu/order path down, the exact outage cache-aside exists to prevent. Or a team cached payment status and served stale "successful" during a gateway outage → cooked 200 unpaid orders.
- **Cross-stack equivalents:** The classification is language-agnostic. Cache-aside fall-through = Spring `@Cacheable` with a DB fallback / any cache client guarded by a null-object; backpressure = reactive streams (Project Reactor/RxJava), Kafka consumer-lag alerts, or Go channel bounds; edge load-shedding = gateway/Envoy rate-limit + 429/503 with `Retry-After`.

## Revisit When
Any new dependency is added (write its rule), or when the measured cache-down p95 breaches the order SLO (then, and only then, make Redis HA).

## References
- ADR-018 (Cache-aside + Redis-down fall-through)
- ADR-019 (Single-flight stampede lock — the hot-key bound)
- ADR-037 (Local read replica)
- ADR-043 (Circuit breaker)
- ADR-035 (Gateway edge rate-limit)
- ADR-027 (Kafka — why a down Payment makes the order *wait*)
