# ADR-048: Response Compression + Conditional GET (ETag/304)

**Date:** 2026-07-12
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Two independent, cheap wins for the exact same read paths Day 6 already made fast (Redis
cache-aside, ADR-018): shrinking what goes over the wire, and skipping the transfer entirely when
the client already has the current copy. Neither needs new infrastructure — both are standard
ASP.NET Core middleware/filters on top of what already exists.

## Decision

**Enable response compression (Brotli preferred, gzip fallback) globally, and add conditional GET
(ETag + `If-None-Match` -> 304) on the cacheable restaurant/menu reads specifically.**

- `AddResponseCompression` with `EnableForHttps = true`; Brotli and Gzip providers at
  `CompressionLevel.Fastest` (CPU-for-bytes is the right trade at Tadka's request volume — revisit
  if a profiler ever disagrees).
- A small `ETagFilterAttribute` (action filter, not global middleware) hashes the serialized
  response body (SHA-256), sets `ETag`, and short-circuits to `304 Not Modified` with an EMPTY
  body when the client's `If-None-Match` already matches. Applied ONLY to
  `RestaurantsController.GetAll` and `GetMenu` — safe, cacheable, non-personalized reads. Never
  on writes, never on anything customer-specific (an order response is not a candidate).

## Consequences

### Positive
- Compression: every list/menu response shrinks (exact ratio depends on payload — JSON compresses
  well, commonly 60-80% smaller) at negligible CPU cost.
- Conditional GET: a client that already has the current menu sends a request and gets a 304 with
  no body, instead of re-downloading the full payload it already has.
- Both are transparent to callers that don't participate (no `Accept-Encoding` -> uncompressed; no
  `If-None-Match` -> normal 200) — no breaking change to any existing client.

### Negative
- ETag freshness is bounded by request rate, not push: the FIRST request after a change still
  gets a full 200 (correct — the client's cached copy really is stale). This is complementary to,
  not a replacement for, the Redis cache-aside TTL/invalidation (ADR-018) — different layers,
  different jobs (ETag saves the client a download; Redis saves the server a DB query).
- The action-filter approach buffers and hashes the WHOLE response body before deciding — cheap
  at Tadka's payload sizes, would need a different (streaming) approach for genuinely large
  responses.
- An edge cache in front of this (see the CDN-emulation beat) does NOT automatically respect this
  ETag — it has its own, separate TTL-based freshness story. Two caches, two invalidation
  mechanisms, is itself a lesson (see the edge-cache ADR).

### Risks
- None material at this scale. If action-filter hashing ever shows up in a profile, the fix is
  narrowing scope further (already narrow) or moving to a cheaper hash — not architectural.

## Alternatives Considered

### Option A: `Cache-Control: max-age` only, no ETag
- Simpler, but the client can't validate freshness after `max-age` expires without a full
  re-fetch. ETag lets a client ask "is my copy still good?" cheaply even after the TTL passes.
- Rejected as *incomplete*, not wrong — `Cache-Control: public, must-revalidate` is set alongside
  the ETag specifically so a client always revalidates rather than trusting a stale local TTL.
  `public`, not `private`: these reads aren't personalized, so a shared/edge cache (ADR-050) is
  allowed to store them too — `private` would (correctly, per HTTP semantics) block that.

### Option B: Weak ETags computed from a version/timestamp column, not a body hash
- Cheaper to compute (no serialize+hash), but requires a version/timestamp on every cached
  resource and doesn't naturally cover computed/joined responses (menu = restaurant + items).
- Rejected for now: body-hash ETags work uniformly across every cacheable response shape without
  per-entity plumbing; revisit if hashing cost ever actually shows up in profiling.

## References
- ADR-018: Redis cache-aside (the server-side cache this complements, not replaces)
- `Filters/ETagFilterAttribute.cs`, `Program.cs` (`AddResponseCompression`)
- Break kit: `cohort-prep/day-06/break-kit-day-06.md`
