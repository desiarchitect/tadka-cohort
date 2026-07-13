# ADR-031: Authorization — RBAC + Resource Ownership

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

With authentication in place (ADR-030) to establish identity, the system must enforce authorization ("what may you do?"). Simple Role-Based Access Control (RBAC) is insufficient. While a user might have a `RestaurantOwner` role, they must only be allowed to edit their *own* restaurant's menu. A `Customer` must only view their *own* orders. Furthermore, in a distributed architecture, we must define exactly where these authorization checks occur.

## Decision

We will implement a hybrid authorization model utilizing **RBAC combined with Resource-Based Ownership**, validated independently at each service boundary.

1. **RBAC:** The `role` claim in the JWT will be used for coarse-grained capability checks (e.g., `Customer`, `RestaurantOwner`, `DeliveryAgent`, `Admin`).
2. **Resource-Ownership:** Service-level authorization handlers will evaluate ownership rules. For example, when fetching an order, the `CustomerId` on the resource must match the `sub` claim in the JWT. For menu updates, the resource's `RestaurantId` must match the owner's `restaurantId` claim. Access violations will return `403 Forbidden`.
3. **Per-Service Validation:** Authorization will be enforced directly within each microservice (e.g., Ordering, Payment) rather than solely relying on an API Gateway to forward user context. Every service must validate the JWT signature and evaluate claims itself.

## Consequences

### Positive
- **Defense in Depth:** The network is not treated as a trust boundary. If a malicious actor bypasses the edge or issues internal requests, services still independently reject unauthorized traffic.
- **Simplicity:** Roles handle broad capabilities, while explicit ownership checks cover the critical multi-tenant data isolation requirements without the overhead of a complex policy engine.

### Negative / Risks
- **Custom Code:** Resource ownership rules require custom logic in each service. As the number of roles or resource types grows, this can become a maintenance burden.
- **Distributed Configuration:** All services must have access to the JWT signing keys (pushing the urgency for asymmetric RS256 keys).

## Alternatives Considered
- **Gateway-Only Authorization:** Terminating auth at a gateway and forwarding an `X-User-Id` header was rejected. This creates a critical vulnerability where internal network access allows full system compromise via header forgery.
- **Attribute-Based Access Control (ABAC) / Policy Engines (OPA):** Rejected as premature optimization. While externalized policy engines (like Open Policy Agent) are powerful, they introduce unnecessary operational complexity for our current access patterns.
- **Relationship-Based Access Control (ReBAC):** Tools like OpenFGA or Google Zanzibar are designed for deep hierarchical sharing (e.g., Google Drive). Tadka's ownership model is flat and does not warrant this complexity.

## Revisit When
We will adopt a centralized Policy Engine (e.g., OPA/Rego) or ABAC when authorization rules become highly contextual (e.g., time-of-day restrictions, transaction amount limits) or when compliance requires policies to be audited and managed outside of application code.
