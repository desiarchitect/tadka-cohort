# ADR-019: Cache Stampede Protection via a Single-Flight Redis Lock

**Date:** 2026-06-02
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Cache-aside (ADR-018) removes repeated reads — until a hot key **expires**. At the dinner rush, the moment a popular restaurant's menu key hits its TTL, the thousands of in-flight requests for it **all miss at the same instant** and stampede the database simultaneously (a thundering herd). For a few hundred milliseconds the DB is hit harder than if there were no cache at all, and p99 spikes. The cache made the common case fast but created a sharp failure at the expiry boundary.

We need exactly **one** caller to refresh a missed hot key while the others wait briefly, instead of all of them piling onto the DB.

## Decision

**Single-flight refresh guarded by a short Redis lock.** Inside `ICacheService.GetOrSetAsync`, on a miss:

1. Try to acquire a lock with `SET lock:{key} {token} NX EX {ttl}` (atomic "set if not exists" + expiry).
2. **Winner:** query the DB, populate the cache, release the lock (delete it **only if the stored token is still ours** — so we never release someone else's lock).
3. **Losers:** wait a short interval and re-read the cache (now populated by the winner). If still empty after a bounded retry, fall through to the DB (correctness over purity).

The lock's own TTL is the safety valve: if the winner crashes mid-refresh, the lock auto-expires and the next caller takes over. Probabilistic early expiration (the Netflix approach — refresh slightly before TTL with rising probability under load) is the documented alternative; we choose the lock because it establishes a clear **distributed-lock primitive** that we can reuse across our architecture (e.g., for idempotency and leader election) and is simpler to reason about.

## Consequences

### Positive
- The DB sees ~1 refresh per hot-key expiry instead of thousands — the stampede is gone.
- Reuses the same StackExchange.Redis client (ADR-018); no new dependency.
- Establishes the SETNX distributed-lock pattern which our engineering team can reuse (idempotency at extraction, leader election, etc.).

### Negative / Risks
- Lock losers pay a small added latency (a wait + a re-read). Negligible vs a DB herd.
- **Lock TTL must exceed the refresh time**, or a slow refresh's lock expires and a second caller starts a parallel refresh (two DB hits, not a thundering herd — tolerable, but sized to avoid). 
- A naive release (`DEL` without the token check) could delete a *different* caller's lock. **Mitigation:** the owns-it token check on release.

### Failure modes (CTO review)
- **Hot key is the canonical stampede trigger.** The herd only forms on a key that *many* callers want at the instant it expires — i.e. a celebrity-restaurant menu (ADR-018). The single-flight lock is precisely the hot-key defence: one caller refreshes, the rest wait. If the *lock key itself* becomes the bottleneck, escalate to probabilistic early expiry / stale-while-revalidate (Revisit), or front Redis with an in-process L1 cache.
- **Cache poisoning is out of scope for the lock — guard it upstream.** The lock controls *who refreshes*, not *what is stored*. Only ever cache values computed from our own DB + validated input, keyed by validated params — never attacker-controlled payloads (see ADR-018 failure modes). A poisoned value would be served to the whole herd until TTL.

### Cost (₹ / effort)
A few lines in the cache service (one `SET NX EX`, a short retry loop, a guarded release). No infra cost. Cheap insurance against a self-inflicted DB spike at every TTL boundary.

## Alternatives Considered
- **Do nothing (accept the stampede):** fine at low traffic, but at dinner-rush concurrency the expiry-boundary spike is real and visible. Rejected for a hot key.
- **Probabilistic early expiration:** elegant — the key never hard-expires under load — but subtler to implement and reason about. Named as the alternative; revisit if lock contention itself becomes hot.
- **Never expire (refresh only on write):** removes the stampede entirely but risks unbounded staleness if an invalidation is missed (no TTL safety net). Rejected; TTL + lock is the balance.
- **A full Redlock implementation:** multi-node distributed-lock correctness is overkill for a single-Redis cache refresh. Rejected as over-engineering at this scale.

## References
- ADR-018 (cache-aside — the pattern this protects), ADR-011 (idempotency — the other "do-this-once" primitive)
- `Infrastructure/Caching/RedisCacheService.cs` (`GetOrSetAsync`), `docs/scaling-decision-tree.md`
- `docs/incidents/inc-102-thundering-herd.md` (the stampede post-mortem)

## Revisit When
If the **lock key itself** becomes a contention hot spot (extreme traffic on one key), switch that key to **probabilistic early expiration** or **stale-while-revalidate**. At multi-node Redis (Redis Cluster) or cross-service locking, reconsider whether a proper distributed-lock library (Redlock) is warranted.
