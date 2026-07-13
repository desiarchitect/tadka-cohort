# ADR-054: Edge Cache Emulation + Signed URLs

**Date:** 2026-07-12
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

While static assets are served efficiently, we need a strategy to offload API GET requests for high-traffic endpoints at the edge, independent of the origin's internal caching layers (ADR-018's Redis cache, ADR-048's ETag). Additionally, we require a mechanism to grant temporary access to specific resources (like invoices) without requiring a full login session. These are standard problems solved by CDN-level caching and signed URLs (similar to S3 presigned URLs or CloudFront signed URLs).

## Decision

**Add an nginx `proxy_cache` zone on the scale-out load balancer (ADR-047) in front of
`GET /api/v1/restaurants`, and a small HMAC-based signed-URL mechanism for the order invoice
endpoint.**

- **Edge cache:** `proxy_cache_path` + `proxy_cache` on the restaurant list route, 30s TTL. `X-Edge-Cache: HIT|MISS|EXPIRED` exposed via `$upstream_cache_status` so the response
  itself shows whether the load balancer or the origin answered.
- **The edge cache does NOT know about ADR-048's ETag.** It caches the raw response for its own
  TTL — updating a restaurant's menu invalidates the app's Redis cache (ADR-018)
  immediately, but the edge cache keeps serving its stale copy until the TTL expires regardless. The trade-off of maintaining two independent caching layers is accepted.
- **Signed URLs (`UrlSigner`):** HMAC-SHA256 over `"{resourceId}|{expiresAtUnixSeconds}"`.
  `POST /api/v1/orders/{id}/invoice/sign` issues a URL valid for 5 minutes;
  `GET /api/v1/orders/{id}/invoice?sig=&exp=` validates the signature (fixed-time comparison) and
  expiry before returning anything. No server-side token state to store or revoke — the trade-off
  is that a leaked signed URL is valid until it naturally expires.

## Consequences

### Positive
- Edge caching provides immediate scale-out capacity for read-heavy endpoints like restaurant discovery.
- Signed URLs require no session state or JWT, providing a resource-specific, time-boxed capability.

### Negative
- Edge cache: `proxy_cache_key "$request_uri"` means query-string variations (pagination, city
  filter) are cached as distinct entries.
- Signed URLs: no revocation before expiry. A 5-minute window bounds the blast radius of a leaked
  link but doesn't eliminate it.
- Two independent caching layers (edge + app-level) introduce more operational complexity and potential cache coherency issues.

### Risks
- A misconfigured edge TTL longer than a business can tolerate for "how stale can a price be" could lead to customer complaints. The 30s TTL must be carefully tuned based on product requirements.

## Alternatives Considered

### Option A: Wire the edge cache to purge on the same event that invalidates Redis (ADR-018)
- Pros: Would eliminate the staleness window entirely.
- Cons: Coupling an edge cache's invalidation to app-level cache events introduces significant distributed systems complexity (cache invalidation fan-out) and brittle infrastructure dependencies.
- Why rejected: The 30-second eventual consistency window is acceptable for restaurant listings.

### Option B: JWT-gated invoice endpoint instead of signed URLs
- Pros: Centralized authentication model.
- Cons: Signed URLs are better suited for capability-style access like sharing a receipt, where requiring the recipient to log in creates friction.
- Why rejected: Signed URLs will coexist with JWT authentication for specific use cases.

## References
- ADR-018 (Redis cache-aside), ADR-047 (Stateless scale-out nginx LB), ADR-048 (Conditional GET)
- `docker/nginx/lb.conf` (`proxy_cache` zone), `Infrastructure/Security/UrlSigner.cs`,
  `Controllers/OrderInvoiceController.cs`
