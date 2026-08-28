# ADR-006: RFC 7807 Problem Details for Errors

**Date:** 2026-05-30
**Status:** Accepted
**Deciders:** Tadka Engineering Team

**Topic:** What shape do errors have, and which status codes mean "retry this same request"?

**Options:**
1. Everything is 200 with `{ success: false }` in the body.
2. HTTP's vocabulary: 400 malformed, 404 gone, 422 valid request / domain said no, 409 race (Day 4). One body shape (RFC 7807).
3. Custom `{ code, message }` or numeric error catalogues.
4. GraphQL-style `errors[]`, ignore HTTP status.

**Choice:** Option 2. ASP.NET `ProblemDetails`. Field errors: 400 with an `errors` map. Domain refusals: 422, no `errors` map. One middleware, every endpoint.

**Why:** Load balancer, CDN, retry library, and monitoring see the **number**, not `detail`. "Can the client send this same request and get a different answer?" is the only question that number must answer. One handler on the client, not one per endpoint. RFC 7807 is the IETF shape; `type` is for machines, `title`/`detail` for humans (never branch on the message text).

**Trade-off:** Verbose vs `{ error: "no" }`. Every new failure needs a `type` URI. Clients handle success **and** error paths (we rejected always-200). Discipline lives in middleware, not in each controller.

**Failure mode:** One controller bypasses the middleware and returns a snowflake JSON. Then we have two standards, which is worse than one ugly standard. Bypass must fail in tests, not in code review.

**Revisit when:** A public API product needs a published `type` catalogue with stable URIs. Not when someone prefers shorter JSON.
