# ADR-049: Distributed (Redis-Backed) Rate Limiting

**Date:** 2026-07-12
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Currently, the primary caller identity available at the edge is IP address. Under a horizontally scaled deployment (ADR-047), a rate limiter that counts in local process memory would give every caller N× the intended limit — one independent counter per replica, none of them aware of the others. A limit that isn't enforced consistently across every replica isn't really a limit and exposes the system to abuse.

## Decision

**Implement a per-IP, Redis-backed rate-limiting middleware, with the counting algorithm selectable via config (`RateLimit:Algorithm=FixedWindow` default, or `SlidingWindow`).**

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
- The limit is strictly enforced globally across all replicas.
- Algorithm choice is a config flag, allowing us to tune burst tolerance vs. Redis CPU overhead per endpoint.
- `Retry-After` is honest per-algorithm, not a fixed guess — a well-behaved client backs off for exactly as long as it actually needs to.

### Negative
- Sliding window costs more per check (ZADD/ZREMRANGEBYSCORE/ZCARD vs one INCR) and more memory
  (one sorted-set entry per allowed request within the window, vs one integer).
- IP-based limiting is coarse: NAT'd users behind one IP share a limit; when user authentication is introduced, per-user limiting becomes possible as an upgrade to this same lever.
- Fixed window's boundary-burst is a real gap; it's the correct default ONLY because it's cheaper and the burst window is short-lived, not because it's more "correct."

### Risks
- A Redis outage currently means the app can't rate-limit at all (the middleware would need
  Redis to be reachable) — same "performance dependency, not correctness dependency" question the
  cache and tracking backplane already answer differently (they degrade gracefully). Acceptable
  here: a rate limiter existing to protect the app from overload failing open under a Redis
  outage is the safer failure mode than failing closed and taking the app down itself.

## Alternatives Considered

### Option A: Per-instance in-memory limiter (no Redis)
- Simplest, zero extra infrastructure.
- Rejected: Under horizontal scale, this silently multiplies the effective limit by the replica count — failing the core requirement of a global rate limit.

### Option B: Token bucket
- Smooths bursts better than either window approach (allows a burst up to the bucket size, then a steady refill rate).
- Not implemented yet: Fixed/Sliding window algorithms are simpler to build as a starting point. Token bucket solves burst tolerance more elegantly and will be considered if Fixed Window burst issues become a production problem.

## References
- ADR-018/019/020 (The optional-infra pattern this follows)
- ADR-047 (Scale-out - the reason a distributed limiter is necessary here)
- Implementation: `Infrastructure/RateLimiting/*`, `Middleware/RateLimitingMiddleware.cs`

## Revisit When
When JWT/Authentication is rolled out, upgrade the limiter's identity key from IP to authenticated user id. If we get a real burst-tolerance requirement, implement a true token bucket algorithm.
