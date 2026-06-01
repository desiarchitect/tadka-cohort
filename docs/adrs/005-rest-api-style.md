# ADR-005: REST for Client-Facing API

**Date:** 2026-05-30
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Tadka needs an API style for its client-facing endpoints consumed by the web app, mobile apps, and potentially third-party integrations. The monolith (ADR-002) has ~20 endpoints across 5 domains. The API will be consumed by frontend developers on the team and eventually by mobile developers.

80% of Tadka's operations are CRUD: list restaurants, get menu, create order, check delivery status. The remaining 20% are domain actions: cancel order, assign delivery agent, initiate refund.

## Decision

Use REST (resource-oriented, HTTP verbs) for all client-facing API endpoints. Use controllers pattern in ASP.NET Core. Domain actions that don't map cleanly to CRUD use explicit POST endpoints (e.g., `POST /api/orders/{orderId}/cancel`).

## Consequences

### Positive

- **Natural CRUD mapping.** `GET /api/restaurants`, `POST /api/orders`, `DELETE /api/payments/{id}` maps directly to CRUD operations. 80% of endpoints write themselves.
- **Team familiarity.** Every developer on the team has built REST APIs. Zero learning curve. Students in the cohort know REST from prior experience.
- **Tooling support.** Scalar UI generates interactive docs from OpenAPI. REST clients (Postman, Insomnia, curl) work out of the box. Browser dev tools show requests clearly.
- **Cacheability.** GET requests are naturally cacheable. HTTP caching headers work as expected. CDN integration for public endpoints is straightforward.
- **Debuggability.** Every request is human-readable. `GET /api/orders/550e8400-...` is self-documenting. No binary protocols to decode.

### Negative

- **Chatty API risk.** Rendering a restaurant detail page might need 3 calls: restaurant details, menu items, reviews. GraphQL would do this in one query. For a monolith, this is less painful since all "calls" are in-process method calls.
- **Over/under-fetching.** REST returns fixed response shapes. The mobile app gets the same response as the web app, even if it only needs 3 of 10 fields. No field selection without custom query parameters.
- **Domain actions feel forced.** "Cancel order" is not a resource. `POST /api/orders/{orderId}/cancel` is technically RPC-over-HTTP, not pure REST. Purists complain. Pragmatists ship.

### Risks

- **Risk:** REST's fixed response shapes lead to multiple "view-specific" endpoints as the frontend evolves. **Mitigation:** Design response shapes to include what most consumers need. Add sparse fieldsets (`?fields=id,name,status`) if it becomes a problem. Consider BFF (Backend for Frontend) pattern in Week 3.
- **Risk:** Internal service-to-service calls using REST are chatty and slow. **Mitigation:** Internal services will use gRPC (Week 3). REST is for external clients only.

## Alternatives Considered

### Option A: GraphQL
- Pros: Single endpoint, client specifies exactly the fields it needs. Solves over-fetching and under-fetching. Great for mobile apps with bandwidth constraints.
- Cons: Adds complexity: schema stitching, N+1 query problems (need DataLoader), caching is harder (POST requests aren't HTTP-cacheable), error handling differs from HTTP conventions. Learning curve for the team.
- Why rejected: Solves a real problem we don't have yet. In a monolith, over-fetching is cheap (no network hop for a JOIN). We'll revisit GraphQL when we have multiple services and the mobile app needs flexible queries.

### Option B: gRPC
- Pros: Binary protocol (protobuf), excellent performance, strongly typed contracts, bidirectional streaming.
- Cons: Not browser-friendly (needs gRPC-Web proxy). Protobuf messages are not human-readable. Tooling for debugging is weaker than REST. Overkill for CRUD.
- Why rejected: Perfect for internal service-to-service calls (Week 3), terrible for browser clients. Tadka's web app needs a browser-friendly API.

### Option C: JSON-RPC
- Pros: Simple method-based calls (`{"method": "createOrder", "params": {...}}`). No URL design needed.
- Cons: Loses HTTP semantics (no GET vs POST distinction, no status codes, no caching). Single endpoint makes routing and middleware harder. Non-standard in the .NET ecosystem.
- Why rejected: Throws away HTTP's built-in features (caching, status codes, method safety) for no benefit over REST.

## References

- ADR-002: Start with a Monolith (REST is sufficient for a monolith's client-facing API)
- [REST API Design Best Practices](https://learn.microsoft.com/en-us/azure/architecture/best-practices/api-design)
- Tadka API Design Guide: `docs/api-design-guide.md`

## Revisit When

When load testing or business requirements prove this decision is a bottleneck, or when specific pain points mentioned in 'Risks' are realized.
