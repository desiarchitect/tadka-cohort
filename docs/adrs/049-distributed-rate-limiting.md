# ADR-049: Distributed (Redis-Backed) Rate Limiting

**Date:** 2026-07-12
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

No JWT exists yet (Day 10 adds auth), so the only caller identity available is IP. More
importantly: under the Day 6 scale-out profile (ADR-047), a rate limiter that counts in local
process memory would give every caller 3x the intended limit — one independent counter per
replica, none of them aware of the others. A limit that isn't enforced consistently across every
replica isn't really a limit.

## Decision

**Add a per-IP, Redis-backed rate-limiting middleware, with the counting algorithm selectable
via config (`RateLimit:Algorithm=FixedWindow` default, or `SlidingWindow`) so the break kit can
run the identical load pattern against both and compare.**

- **Fixed window** (`RedisFixedWindowRateLimiter`): `INCR` a counter keyed by
  `(identity, current-60s-bucket)`, `EXPIRE` on first hit. One Redis round trip via a Lua script
  for atomicity. Cheap; the trade-off is a boundary-burst: a caller can send the full limit at
  59.9s into a window and the full limit again at 60.1s — 2x the intended rate in a 200ms window.
- **Sliding window** (`RedisSlidingWindowRateLimiter`): a Redis sorted set per identity, each
  allowed request scored by its own timestamp; a check evicts entries older than the window, then
  counts what's left. No window-alignment, so no boundary burst — at the cost of a sorted-set
  operation instead of a single counter.
- Every 429 carries `Retry-After` (seconds), computed from the algorithm's own state (remaining
  TTL for fixed window; time until the oldest entry ages out for sliding window) — never a
  guessed constant.
- No Redis configured -> `NullRateLimiter` (always allow) — optional infra, matching the
  cache/tracking pattern (ADR-018/020): local dev and the test suite are unaffected.

## Consequences

### Positive
- The limit is real across every replica — this is a direct, demoable payoff of the scale-out
  profile: run the SAME limiter as in-process-memory (a version students can imagine wiring
  themselves) vs Redis-backed, and only one of them actually enforces the stated number under 3
  replicas.
- Algorithm choice is a config flag, not a rewrite — the comparison IS the deliverable, not a
  chosen "winner."
- `Retry-After` is honest per-algorithm, not a fixed guess — a well-behaved client backs off for
  exactly as long as it actually needs to.

### Negative
- Sliding window costs more per check (ZADD/ZREMRANGEBYSCORE/ZCARD vs one INCR) and more memory
  (one sorted-set entry per allowed request within the window, vs one integer).
- IP-based limiting is coarse: NAT'd users behind one IP share a limit; Day 10's JWT makes
  per-user limiting possible as an upgrade to this same lever, not a different mechanism.
- Fixed window's boundary-burst is a real, demonstrable gap — documented here rather than hidden;
  it's the correct default ONLY because it's cheaper and the burst window is short-lived, not
  because it's more "correct."

### Risks
- A Redis outage currently means the app can't rate-limit at all (the middleware would need
  Redis to be reachable) — same "performance dependency, not correctness dependency" question the
  cache and tracking backplane already answer differently (they degrade gracefully). Acceptable
  here: a rate limiter existing to protect the app from overload failing open under a Redis
  outage is the safer failure mode than failing closed and taking the app down itself.

## Alternatives Considered

### Option A: Per-instance in-memory limiter (no Redis)
- Simplest, zero extra infrastructure.
- Rejected as the demonstrated BREAK: under 3 replicas this silently multiplies the effective
  limit by the replica count — exactly the bug this ADR exists to prevent, not a viable option.

### Option B: Token bucket
- Smooths bursts better than either window approach (allows a burst up to the bucket size, then a
  steady refill rate) and is what the pre-existing `rate-limiter-toy` demonstrates conceptually.
- Not implemented as a third live algorithm here: the break kit's comparison is specifically
  about the *boundary-burst* difference between fixed and sliding windows (the failure mode this
  ADR's evidence captures); token bucket solves a different problem (burst tolerance) and stays a
  concept-only comparison via the toy, to avoid diluting the specific lesson with a third axis.

## References
- ADR-018/019/020 (the optional-infra pattern this follows), ADR-047 (scale-out - the reason a
  distributed limiter is necessary here, not optional polish)
- `Infrastructure/RateLimiting/*`, `Middleware/RateLimitingMiddleware.cs`
- `toydemo/day-06-cache-realtime/rate-limiter-toy/` (token bucket, concept-only comparison)
- Break kit: `cohort-prep/day-06/break-kit-day-06.md`

## Revisit When
Day 10 adds JWT auth -> upgrade the limiter's identity key from IP to authenticated user id
(same Redis-backed mechanism, different key). A real burst-tolerance requirement -> implement
token bucket for real, not just as a toy comparison.
