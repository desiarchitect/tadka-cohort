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

---

## Addendum (2026-10-02): YARP or Azure API Management in the cloud demo?

**Context.** The Azure session (ADR-064) runs `Tadka.Gateway` (YARP) as the only external ingress, behind Front Door. Azure also sells managed gateways. The question: should the cloud demo swap YARP for Azure API Management (APIM)? Note the "Revisit When" above names "Azure Application Gateway + APIM" together; they are two different products. Application Gateway is a regional layer-7 load balancer with a WAF. APIM is an API product layer (keys, plans, quotas, policies, developer portal). Here we compare against APIM, since that is the real alternative to YARP's job.

### What YARP does for Tadka today
Path routing to the four services; a per-IP rate limiter; the weighted canary for Restaurant (ADR-061); the Front Door origin lock (`X-Azure-FDID`, ADR-064); and, because it is ordinary C#, tests (`tests/Tadka.Gateway.Tests`). In Azure it is one small container app, so its extra cost is close to nothing.

### Options

| Option | Cost (approximate list prices, **not verified here**; check the Azure pricing calculator) | Fit for this demo |
|---|---|---|
| **YARP container app (chosen)** | a fraction of a vCPU, a few hundred rupees a month if left on | Same code as local, same tests, reaches the internal services directly |
| APIM Consumption | pay per call (about $3.50 per million, first million free each month) | **Cannot reach our private backends.** Our services use internal ingress in a VNet-integrated environment |
| APIM Developer | about $48 a month (about $0.07 an hour) | VNet support, but no SLA, and provisioning takes 30 to 45 minutes or more |
| APIM Basic v2 | about $150 a month (about $0.2 an hour) | Fast to provision, VNet integration, about 50 to 100% on top of a session's estimated cost |
| APIM Standard v2 | about $700 a month | More than this demo needs |
| Azure Application Gateway | hourly fee plus capacity units | Duplicates what Front Door (WAF, TLS) and the Container Apps ingress already do |
| Container Apps ingress alone | included | No path rewrite, canary or rate limit logic of our own |

APIM bills by the hour, so a 4-hour session is affordable (roughly ₹20 on Developer, ₹70 on Basic v2). The real exposure is a forgotten teardown, and the budget alert arrives 8 to 24 hours late.

### Decision
**Keep YARP as the gateway in the Azure demo.** Treat APIM as the named production option, and teach it in the option space.

### Why
1. **Local and cloud stay the same.** Day 11 and Day 12 teach YARP, and students run it locally. A different gateway in the cloud would show a demo that does not match the code they ran, and it would orphan `Tadka.Gateway` and its tests.
2. **The private-backend constraint rules out the cheap tier.** Only the VNet-capable APIM tiers can reach internal Container Apps, and those are where the cost and setup time come from.
3. **Most of YARP's job is already done in Azure.** Front Door provides the WAF, rate limit and cache; Container Apps provides load balancing. APIM would mostly add API-product features (keys, plans, quotas, a portal) that Tadka has no consumers for.
4. **Port effort is real.** Each YARP feature becomes policy XML in Terraform:

   | YARP today | In APIM |
   |---|---|
   | JSON routes and clusters | APIs, operations and backends |
   | `FixedWindowRateLimiter` per IP | `rate-limit-by-key` (behind Front Door the client IP is in a forwarded header) |
   | `WeightedCanaryPolicy` (ADR-061) | a random choice plus `set-backend-service` policy, or Container Apps traffic splitting |
   | `FrontDoorOriginLock` middleware | `check-header` policy on `X-Azure-FDID` |
   | SSE live tracking | response buffering must be turned off and the tier's connection timeout checked |

### Trade-off
We keep the one thing students can run and read, and we give up managed high availability and API-product features. YARP is something we operate: scale-out, patching and uptime are ours.

### Failure modes
- **YARP is a single point of failure.** A bad deploy or a crash loop takes the whole API down. Mitigation in the demo: HTTP-based autoscaling (1 to 5 replicas) and Front Door health probes.
- **Per-replica rate limits.** The in-memory limiter is per replica, which is why the cloud demo moves the real per-IP limit to the Front Door WAF.
- **If we had chosen APIM and picked the wrong tier,** the Consumption tier would fail at deploy time (it cannot reach the private services), and a classic Developer deploy could eat 45 minutes of class setup.

### Revisit when
Move to APIM (or another managed gateway) when any of these is true:
- external partners need API keys, usage plans or quotas;
- many teams need one shared policy layer for auth and throttling;
- you need a built-in developer portal;
- the platform team no longer wants to operate the gateway itself.

If we ever try it, run it as a sandbox comparison on Basic v2 first, and test SSE (live tracking) before anything else. That is the highest-risk behavior.

### Cross-stack equivalents
The pattern (a thin edge for routing and rate limiting, services still validate the JWT) is the same in Spring Cloud Gateway, Kong, Envoy, Traefik, nginx and the cloud-managed gateways. Only the configuration language changes.
