# ADR-031: Authorization — RBAC + Resource-Ownership, validated per-service

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Authentication (ADR-030) answers "who are you?"; now "**what may you do?**" A role check alone is not enough: a `RestaurantOwner` is allowed to edit menus *in general*, but must not edit **another** restaurant's menu; a `Customer` may view orders, but only **their own**. And there's a second question — **where** is authorization enforced (we have no gateway).

## The model menu (teach the map, then pick)

| Model | Decides on | Best for | Why-not (yet) here |
|---|---|---|---|
| **RBAC** (roles) ✅ | the user's role | coarse capabilities (4 roles) | "which restaurant?" needs more than a role |
| **Resource-based / ownership** ✅ | does this subject own this object? | "my order / my restaurant" | — (paired with RBAC) |
| **ABAC** (attributes/context) | subject+resource+env attributes | rules like "approve > ₹10k only 9–5" | overkill for 4 roles + ownership; needs an attribute/policy evaluator |
| **ReBAC** (relationship graph) | relationships (Google Zanzibar / OpenFGA) | deep sharing/hierarchy ("folder → doc → viewer") | Tadka's relationships are shallow |
| **PBAC / policy engine (OPA + Rego)** | externalized policy | policy owned by non-engineers, many services, audited | premature; 1–2 services, simple rules |
| **ACL** (per-object lists) | explicit allow/deny per object | fine-grained sharing | maintenance heavy; ownership covers us |

## Decision

**RBAC + resource-based ownership, validated independently in every service.**
- **RBAC:** `[Authorize(Roles=…)]` from the JWT `role` claim gates capabilities (Customer/RestaurantOwner/DeliveryAgent/Admin).
- **Resource-ownership:** an inline ownership check per controller action answers "does *this* user own *this* resource?" (e.g. `RestaurantsController.OwnsOrAdmin(restaurantId)` — `User.IsAdmin() || User.OwnedRestaurantId() == restaurantId`) — an order's `CustomerId` must match `sub`; a menu's restaurant must match the owner's `restaurantId` claim; `Admin` bypasses. Returns **403** (authenticated but not allowed) vs **401** (not authenticated).
- **Validation location (sub-decision):** **per-service**, not gateway-only. Options were (a) gateway validates + forwards `X-User-Id`, (b) **every service validates the JWT itself**, (c) hybrid. We choose (b): the Payment service verifies the same token on its own HTTP endpoints. **Defense in depth** — the network is not a trust boundary; if someone reaches a service directly (no gateway exists yet anyway), it's still protected.

## Consequences
**Positive:** roles cover 80% with one attribute; ownership handlers cover the "whose data?" 20% without a policy engine; per-service validation means no service implicitly trusts a header or the network. **Negative/Risks:** every resource-ownership rule is custom code (fine for 4 roles; becomes a maintenance load at many-roles scale → that's the ABAC/OPA trigger); the shared signing key is in every service (→ RS256). **Failure mode (the classic):** a team validates only at the gateway and forwards a plain `X-User-Id` header; an attacker who reaches a service on the internal network forges the header → full access. Per-service JWT verification kills that. **Cost:** code only.

## Alternatives Considered
- **Gateway-only validation:** convenient, but the gateway is a reverse proxy, not a trust boundary; rejected (defense in depth).
- **ABAC / OPA now:** powerful (context rules, externalized policy) but adds an evaluator + latency for rules we don't have yet; rejected until a real attribute/context rule or non-engineer-owned policy appears.
- **ReBAC (OpenFGA/Zanzibar):** for deep relationship graphs (Drive-style sharing); Tadka's relationships are shallow; rejected.

## Cross-stack equivalents
ASP.NET roles (`[Authorize(Roles=...)]`) + an inline ownership check ≈ **Spring Security** `@PreAuthorize` + `PermissionEvaluator` · **Node** middleware / **CASL** / NestJS Guards · **Go** middleware. Policy engines (stack-neutral): **OPA/Rego**, **OpenFGA/Zanzibar** (ReBAC), **Casbin** (RBAC/ABAC lib for many languages). RBAC→ABAC→ReBAC is the same escalation ladder everywhere.

## References
- ADR-030 (the JWT claims this reads), ADR-032 (PII access is itself an authz concern), ADR-024 (the Payment service that must validate too)
- `cohort-prep/day-10/option-space.md`, `break-kit-day-10.md` (403 cross-owner; forged-token rejected per-service)
- Implementation: monolith `[Authorize(Roles=...)]` + inline `OwnsOrAdmin`/ownership checks in each controller; JWT bearer in the Payment service

## Revisit When
Adopt **ABAC** when a real context rule appears (amount/time/geo); **OPA/PBAC** when policy must be owned/audited outside code or spans many services; **ReBAC (OpenFGA)** if sharing/hierarchy graphs appear. Re-evaluate gateway-vs-service validation when the **API gateway** lands (keep per-service as the floor).
