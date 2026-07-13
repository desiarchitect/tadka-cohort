# ADR-050: Edge Cache Emulation + Signed URLs

**Date:** 2026-07-12
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

"Put a CDN in front of it" is common advice students will hear and repeat. Day 6 has no static
asset serving yet (that's Tadka.Gateway's `wwwroot`, introduced Day 11), so the honest way to
demo a CDN's actual mechanism at this point in the curriculum is what a CDN does for an API: cache
GET responses at the edge, independent of and unaware of the origin's own caching story
(ADR-018's Redis cache, ADR-048's ETag). A related, separate question — how does a client get
temporary access to something without a login session — is answered by the same demo session
using time-limited signed URLs, the mechanism real CDNs and object stores (S3 presigned URLs,
CloudFront signed URLs) both use.

## Decision

**Add an nginx `proxy_cache` zone on the scale-out load balancer (ADR-047) in front of
`GET /api/v1/restaurants`, and a small HMAC-based signed-URL mechanism for the order invoice
endpoint.**

- **Edge cache:** `proxy_cache_path` + `proxy_cache` on the restaurant list route, 30s TTL (short,
  so the "stale after update" window is demoable inside a class session, not a real production
  TTL). `X-Edge-Cache: HIT|MISS|EXPIRED` exposed via `$upstream_cache_status` so the response
  itself shows whether nginx or the origin answered.
- **The edge cache does NOT know about ADR-048's ETag.** It caches the raw response for its own
  TTL, full stop — updating a restaurant's menu invalidates the app's Redis cache (ADR-018)
  immediately, but the edge cache keeps serving ITS stale copy until the TTL expires regardless.
  Two caches, two invalidation stories, is the lesson, not a bug to "fix" by wiring them together
  (that coupling is itself a real production trade-off worth naming, not silently avoiding).
- **Signed URLs (`UrlSigner`):** HMAC-SHA256 over `"{resourceId}|{expiresAtUnixSeconds}"`.
  `POST /api/v1/orders/{id}/invoice/sign` issues a URL valid for 5 minutes;
  `GET /api/v1/orders/{id}/invoice?sig=&exp=` validates the signature (fixed-time comparison) and
  expiry before returning anything. No server-side token state to store or revoke — the trade-off
  is that a leaked signed URL is valid until it naturally expires.

## Consequences

### Positive
- The edge-cache demo is honest about WHAT a CDN actually does (cache responses by URL, on its
  own clock) rather than hand-waving "the CDN makes it fast."
- Signed URLs need no session, no JWT (none exists yet) — a resource-specific, time-boxed
  capability, the same mechanism Day 10's JWT-based auth will layer on top of, not replace.

### Negative
- Edge cache: `proxy_cache_key "$request_uri"` means query-string variations (pagination, city
  filter) are cached as DISTINCT entries — correct, but means cache warmth is per-exact-URL, not
  per-endpoint.
- Signed URLs: no revocation before expiry. A 5-minute window bounds the blast radius of a leaked
  link but doesn't eliminate it — acceptable for a demo/receipt use case, would need short-lived
  + single-use tokens (or a real session) for anything higher-stakes.
- Two independent caching layers (edge + app-level) is genuinely more to reason about operationally
  — that complexity is the point being taught, not an accident.

### Risks
- A misconfigured edge TTL longer than a business can tolerate for "how stale can a price be" is
  a real production incident category — this ADR's 30s is a demo value, explicitly not a
  recommendation for any specific production TTL (that number is a product/business decision).

## Alternatives Considered

### Option A: Wire the edge cache to purge on the same event that invalidates Redis (ADR-018)
- Would eliminate the staleness window entirely.
- Rejected for this beat: coupling an edge cache's invalidation to app-level cache events is
  real, valuable production engineering — but it's a DIFFERENT, harder lesson (cache invalidation
  fan-out) than the one this beat teaches (two independent caches disagree, know why). Worth its
  own future beat, not folded in here.

### Option B: JWT-gated invoice endpoint instead of signed URLs
- The eventual real answer once Day 10 exists.
- Rejected for now: Day 6 has no auth yet, and the signed-URL mechanism itself (time-boxed,
  stateless, tamper-evident) is a distinct, transferable pattern worth teaching on its own —
  it doesn't disappear once JWT exists (S3 presigned URLs coexist with IAM every day).

## References
- ADR-018 (Redis cache-aside — the OTHER cache this one disagrees with), ADR-047 (the load
  balancer the edge cache sits on), ADR-048 (the app-level conditional-GET this is NOT the same as)
- `docker/nginx/lb.conf` (`proxy_cache` zone), `Infrastructure/Security/UrlSigner.cs`,
  `Controllers/OrderInvoiceController.cs`
- Break kit: `cohort-prep/day-06/break-kit-day-06.md`

## Revisit When
Day 10 adds JWT — signed URLs stay for capability-style, no-login access (receipts, shared links);
JWT covers everything that needs a real identity check.
