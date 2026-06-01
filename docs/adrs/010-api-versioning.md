# ADR-010: API Versioning via URL Path (`/api/v1`)

**Date:** 2026-06-01
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Tadka's API is consumed by the web app and (soon) native mobile apps, and after service extraction (Week 4+) by other internal services. **Mobile clients cannot be force-upgraded** — an old app version can stay on a user's phone for months. So the moment we ship a contract, we are committing to not breaking it from under live clients.

We need a versioning strategy decided **before the contract ships** (API-first), not bolted on after the first breaking change. Today the routes are unversioned (`/api/...`), and our own docs disagree (the design guide and copilot-instructions reference `/api/v1` while the code uses `/api`). This ADR resolves that.

## Decision

**URL-path versioning. All routes live under `/api/v1`.**

- **Additive / backward-compatible** changes — new optional request fields, new response fields, new endpoints — do **not** bump the version. Clients ignore fields they don't know.
- **Breaking** changes — removing/renaming a field, changing a type or its semantics, removing an endpoint, tightening validation — ship under a **new major (`/api/v2`)**.
- The previous major is supported **~6 months** after its successor is GA, serving a `Deprecation` / `Sunset` response header during the overlap.

## Consequences

### Positive
- **Obvious and debuggable.** The version is visible in the URL, in logs, in `curl`, in the browser. No hidden negotiation.
- **Trivial to route.** A single route prefix; the future API gateway (Week 5) routes `/api/v1/*` without inspecting headers.
- **Side-by-side majors.** `v1` and `v2` controllers can coexist during migration; consumers pin explicitly and migrate on their own schedule.

### Negative
- **Not "pure" REST.** Purists argue the URI should identify the *resource*, not the representation version; header/content-negotiation is theoretically cleaner.
- **Encourages coarse jumps.** URL majors nudge teams toward big `v1→v2` rewrites rather than fine-grained field-level evolution (mitigated by the additive rule below).

### Risks
- Teams bump the version for *additive* changes out of caution → version sprawl. **Mitigation:** the additive-vs-breaking rule above is explicit and enforced in review.
- Teams sneak a breaking change into `v1` → silent client breakage. **Mitigation:** contract tests (and, post-extraction, consumer-driven contracts) fail the build when `v1`'s shape changes incompatibly.

### Cost (₹ / effort)
Near-zero infrastructure — it's a route prefix. The real cost is **discipline** plus the **6-month overlap window**, during which `v1` and `v2` are both live: double the API surface to test, document, and operate for that period. Budget for it before promising a `v2`.

## Alternatives Considered

### Option A: No versioning (stay on `/api`)
- Pros: simplest possible.
- Cons: any breaking change breaks installed mobile apps with no escape hatch.
- Why rejected: unacceptable the day we have real clients. Versioning is cheapest to add *before* the first consumer, not after.

### Option B: Header / media-type versioning (`Accept: application/vnd.tadka.v1+json`)
- Pros: clean URIs; content negotiation; supports fine-grained representation versioning.
- Cons: invisible in browser/logs; awkward to `curl` and test; easy for clients to get wrong; overkill for our stage.
- Why rejected: the debuggability and routing simplicity of URL versioning win for a team this size. Revisit if we ever need per-resource representation versioning.

### Option C: Query-parameter versioning (`?version=1`)
- Pros: simple to add.
- Cons: easy to omit (what's the default?); interacts badly with caching/proxies; clutters every URL.
- Why rejected: ambiguous defaults and caching pitfalls.

## References
- ADR-005: REST for Client-Facing API
- `docs/api-design-guide.md` §11 (Versioning)
- Stripe / Microsoft REST versioning guidance

## Revisit When
When we need **fine-grained, per-resource representation versioning** (move to header-based for those resources), or when an **API gateway / spec-driven contract (OpenAPI)** standardizes versioning across the extracted services, or when the 6-month dual-running cost stops being worth the URL simplicity.
