# ADR-047: Stateless Scale-Out — Explicit Replicas Behind an nginx Load Balancer

**Date:** 2026-07-12
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

To achieve high availability and handle increased traffic, Tadka must scale beyond a single instance. Running a single instance hides two critical architectural flaws:
1. **Lack of fault tolerance:** A crashed or redeploying instance takes the entire app down.
2. **Hidden stateful dependencies:** In-memory sessions, in-process caches, or local queues silently work on a single instance but break when requests are distributed across multiple instances.

We need to enforce statelessness across the application tier by scaling out behind a load balancer, utilizing our existing externalized state (Redis, Postgres, Kafka).

## Decision

**Deploy 3 explicit monolith replicas (`api-1`/`api-2`/`api-3`, same image, same code) behind an nginx load balancer.**

- **Explicit services over dynamic scaling:** nginx's health-check eviction (`max_fails`/`fail_timeout`) requires stable, known backend addresses to track up/down state. Dynamically resolved single DNS names don't allow nginx to evict individual sick backends effectively without additional configuration.
- **`least_conn` load balancing:** Distribute traffic based on active connections rather than strict round-robin, preventing slow requests from piling up on one instance.
- **`proxy_next_upstream`:** On error/timeout/5xx, a request that hits a dead replica is automatically retried against a live one instead of failing outright.
- **Debugging header:** Every response carries `X-Tadka-Instance` (set from the `INSTANCE_NAME` env var) to simplify tracking which replica served a request during production incidents.

## Consequences

### Positive
- **Fault tolerance:** Instances can be killed or rolled over; requests keep succeeding as nginx evicts and routes around them.
- **Surfaces hidden state:** Exposes any accidental in-process caching or session affinity bugs immediately.

### Negative
- **Network Hop:** Adds a second HTTP hop (nginx -> app). The latency is negligible, but it adds infrastructure complexity.
- **Connection Pool Pressure:** 3x the database/Redis connection pool pressure from the application tier.
- **Startup Migrations:** If replicas run `db.Database.Migrate()` on startup, simultaneous first-boot migration attempts are possible. Migrations must be decoupled into a separate pre-release step (zero-downtime migration pipeline).
### Risks
- Manual replica definitions in compose/config files don't scale automatically. This is acceptable for our current baseline, but true auto-scaling requires a cloud-native compute platform (like ECS, handled in ADR-039).

## Alternatives Considered

### Option A: Dynamic `--scale` / Auto-discovery
- Simpler configuration, no repeated blocks.
- Rejected for our nginx setup because it requires stable backend tracking for `proxy_next_upstream` and health checks. Relying solely on DNS resolution for backends hides the health state from the load balancer.

### Option B: Use YARP Gateway directly
- We will eventually use YARP as our API Gateway to route between *different* microservices (Payment, Delivery, Restaurant). 
- Rejected for simple replica load-balancing: nginx is an industry standard, lightweight, and purpose-built for basic L4/L7 load balancing across replicas of the *same* service.

## References
- ADR-016 (Read replica — another place where knowing "which backend answered" matters)
- ADR-039 (Cloud deployment targeting ECS auto-scaling, the production evolution of this pattern)

## Revisit When
We move fully to a cloud-managed load balancer (e.g., AWS ALB) where explicit manual replica registration is replaced by target group auto-registration via ECS/EKS.
