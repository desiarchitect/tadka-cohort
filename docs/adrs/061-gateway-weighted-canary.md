# ADR-061: Gateway Weighted Canary (Restaurant)

**Date:** 2026-07-13  
**Status:** Accepted  
**Deciders:** Tadka Engineering Team  

## Context

Deploying new versions of our microservices is currently a high-risk event. We deploy the new container image, updating all instances at once. Even with thorough integration testing, bugs that only manifest under real production traffic patterns periodically slip through. If a bug takes down the Restaurant service, orders stop. We need a way to verify new deployments with a small fraction of real production traffic before committing to a full rollout.

## Decision

We will use **YARP (Yet Another Reverse Proxy)** to implement a weighted canary release strategy at the edge.

- The YARP configuration for the `restaurant` cluster will define two destinations: `stable` (e.g., port 5260) and `canary` (port 5261).
- We implement a custom `WeightedCanary` routing policy that reads the `Canary:RestaurantPercent` configuration value.
- If set to 5%, YARP will route 5% of incoming Restaurant API requests to the canary instance, and 95% to the stable instances.
- **Rollback:** If telemetry shows elevated 500s or latency on the canary, we simply set the percent to 0, instantly shifting all traffic back to stable without waiting for a container restart or deployment pipeline. (The break kit simulates this via `Restaurant:Buggy=true`).

## Consequences

### Positive
- **Blast Radius Control:** A catastrophic bug in the new release now only affects a small, controlled percentage of traffic instead of causing a platform-wide outage.
- **Production Verification:** We can safely test new logic (like the updated pricing models or new schema migrations) against actual production request shapes.
- **Instant Rollback:** Reverting traffic is a configuration change at the gateway, which is instantaneous.

### Negative / Risks
- **Data Corruption Risk:** While the gateway controls HTTP traffic, if the canary deployment contains a bug that corrupts the shared database, the blast radius is *not* contained. The canary must still use backward-compatible database schemas.
- **Monitoring Dependency:** A canary is useless if we can't observe it. We strictly rely on our OpenTelemetry metrics (ADR-040) to compare the error rates and latency of `stable` vs `canary`.
- **Stateless Requirement:** This strategy requires services to be completely stateless. If a request hits `canary` and a subsequent request hits `stable`, the experience must not break.

## Alternatives Considered

- **Blue/Green Deployment:** Spinning up an entirely new parallel environment (Green) and switching the DNS or load balancer 100% over to it. *Rejected:* It requires provisioning 2x the infrastructure during the deploy window and doesn't allow for a slow, partial bleed of traffic. If Green is broken, 100% of users see the error until rollback.
- **Shadow Traffic / Dark Launching:** Replicating real traffic to the new service without returning the response to the user. *Rejected:* Excellent for read-heavy services, but dangerous for transactional systems like ours without extreme care to isolate side effects (we don't want the shadow service actually charging credit cards).

## Revisit When
We adopt a Service Mesh (like Istio or Linkerd) which can handle advanced L7 traffic splitting and automated canary analysis (automatically rolling back if the error rate deviates) natively, replacing our manual YARP policies.
