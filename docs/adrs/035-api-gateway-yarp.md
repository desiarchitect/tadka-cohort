# ADR-035: API Gateway

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

As we extract domains from the monolith into independent microservices (Payment, Delivery, etc.), the external topology becomes fractured. Client applications (mobile apps, web frontends) are forced to communicate with multiple distinct hostnames and ports. This distributed surface area makes it difficult to enforce cross-cutting edge concerns uniformly, such as rate limiting, TLS termination, CORS policies, and centralized request logging. Furthermore, exposing the internal microservice topology directly to clients creates tight coupling, breaking clients whenever a new service is extracted or internal routing changes.

## Decision

We will introduce an **API Gateway** as the single public entry point for all client traffic.

- **Routing:** The gateway will route traffic based on path prefixes (e.g., `/api/v1/payments/**` to the Payment service, `/api/v1/deliveries/**` to the Delivery service, and default traffic to the monolithic core).
- **Abstraction:** Clients will interact with a single unified API surface. Internal architectural changes and extractions will remain transparent to external consumers.
- **Edge Policies:** The gateway will centralize edge concerns, including global rate limiting, request logging, and TLS termination.
- **Trust Boundary:** The API Gateway will **not** serve as a definitive trust boundary. While it may reject blatantly invalid traffic, all downstream services must continue to perform independent JWT validation and resource authorization (as defined in ADR-031).

## Consequences

### Positive
- **Decoupled Topology:** Internal service extractions and refactoring can proceed without requiring coordinated client application updates.
- **Centralized Edge Security:** A single enforcement point for DDoS mitigation, rate limiting, and global observability.
- **Simplified Client Logic:** Frontends interact with a single domain, avoiding complex CORS configurations and host management.

## Negative / Risks
- **Single Point of Failure:** The gateway becomes a critical infrastructure component. If it goes down, the entire system is inaccessible. It must be highly available.
- **Increased Latency:** Introduces an additional network hop for all API requests.
- **Scope Creep:** There is a constant temptation to push domain business logic (e.g., data aggregation, complex orchestration) into the gateway layer, which leads to an unmaintainable "enterprise service bus" anti-pattern.

## Alternatives Considered
- **Direct Client-to-Service Communication:** Rejected because it tightly couples clients to internal architecture and scatters edge security policies across multiple codebases.
- **Backend-For-Frontend (BFF):** A pattern where specific gateways are built for specific clients (e.g., Web vs. Mobile). Rejected as premature; our current client needs are uniform enough for a single API Gateway.

## Revisit When
As traffic scales in production, we will transition from a custom code-based gateway to a managed cloud native solution (e.g., AWS ALB + API Gateway, or Azure Application Gateway + APIM) for enhanced performance and managed high availability. We will consider the BFF pattern if client data requirements drastically diverge.
