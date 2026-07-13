# ADR-058: Feature Flags (Percentage Rollout)

**Date:** 2026-07-13  
**Status:** Accepted  
**Deciders:** Tadka Engineering Team  

## Context

As the platform grows, big-bang deployments of high-risk changes (e.g., the new dynamic pricing engine, UI overhauls) have become too dangerous. A single bad commit can take down the entire order flow for all users. We need the ability to deploy unfinished or risky paths without a full cutover, allowing us to test in production with a limited blast radius and turn off broken features instantly without waiting for a full rollback deployment.

## Decision

We will implement a custom, lightweight percentage-based feature flag system:

- **Stable hashing:** We compute a stable SHA256 hash of `flagName|userKey` (e.g., the user ID or session ID) to assign the user deterministically into a bucket between 0 and 99.
- **Evaluation:** The feature is enabled for the user if their bucket value is strictly less than the configured percentage rollout.
- **Storage:** Rollout percentages are read from Redis via the `Flags:{name}` key, falling back to local configuration (`appsettings`).
- **Management:** We expose an internal admin endpoint `PUT /api/v1/flags/{name}` to update the percentage in Redis dynamically.

## Consequences

### Positive
- **Consistent User Experience:** Because the hash is stable, a user who is assigned to a feature remains assigned to it across requests. No sticky sessions are required at the load balancer.
- **Instant Kill Switches:** Modifying the Redis key instantly affects all instances of our services, bypassing the deployment pipeline entirely if an incident occurs.
- **Decoupled Deployment and Release:** Code can be safely deployed dark (0% rollout) and enabled gradually.

### Negative / Risks
- **Redis Dependency:** If Redis goes down and local config doesn't match, the flags might fall back to stale or incorrect defaults.
- **Hash Bucket Staleness:** If we need to target specific segments (e.g., "only premium users") rather than random percentages, this naive bucket approach is insufficient.
- **Code Clutter:** Application code must be littered with `if (FeatureEnabled("..."))` blocks, which accrue technical debt if flags aren't cleaned up after full rollout.

## Alternatives Considered

- **Third-Party SaaS (e.g., LaunchDarkly, ConfigCat):** Extremely robust and provides targeting, auditing, and dashboards. However, it introduces an external network dependency (and recurring cost) on our critical path. We opted to build a simple Redis-backed system to avoid the external dependency and cost for our current scale.
- **Simple Boolean Toggles (On/Off):** Easy to implement but doesn't allow for gradual canaries. A 100% rollout that fails causes a 100% outage.
- **Database-backed flags:** Using Postgres to store flags adds unnecessary read load to the primary database on every single request. Redis is vastly superior for this high-read, low-write workload.

## Revisit When
When we require complex segmentation (e.g., targeting specific geographical regions, device types, or user roles), or when the engineering team size grows to the point where an audited UI for flag management is required, we should migrate to a dedicated feature flag SaaS.
