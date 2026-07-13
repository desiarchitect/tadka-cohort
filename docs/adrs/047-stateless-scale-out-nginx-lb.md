# ADR-047: Stateless Scale-Out — Explicit Replicas Behind an nginx Load Balancer

**Date:** 2026-07-12
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Every day so far has run Tadka as a single process. Day 3's own architecture note already
promised "session storage" and "multi-instance" would matter once the app scales beyond one box
— this is where that promise is paid off, using the Day 6 tools already in hand (Redis) rather
than waiting for Day 11's YARP gateway. Two questions a single instance can't answer: (1) does a
crashed/redeploying instance actually take the app down, and (2) does anything the app does
secretly depend on staying on the SAME instance across requests (in-memory session, in-process
cache, local queue)?

## Decision

**Add a `scale-out` docker-compose profile: 3 explicit monolith replicas (`api-1`/`api-2`/`api-3`,
same image, same code) behind an nginx load balancer, off by default.**

- `docker compose --profile scale-out up` — a plain `docker compose up` is unaffected (no
  behavior change on any earlier day's workflow).
- **3 explicit services, not `docker compose --scale`.** nginx's health-check eviction
  (`max_fails`/`fail_timeout`) needs stable, known backend addresses to track down/up state
  against — a dynamically resolved single DNS name (what `--scale` produces) doesn't give nginx
  anything stable to evict.
- **`least_conn` load balancing**, `proxy_next_upstream` on error/timeout/5xx so a request that
  hits a dead replica gets retried against a live one instead of failing outright.
- Every response carries `X-Tadka-Instance` (set from the `INSTANCE_NAME` env var, container-side)
  so a demo can show exactly which replica answered.

## Consequences

### Positive
- Proves fault tolerance a single-instance setup can't: kill one replica mid-load, requests keep
  succeeding (nginx evicts it, routes around it, re-admits it once healthy again).
- Surfaces hidden state immediately: the very first thing 3 replicas expose is that the
  in-process cache fallback (see ADR-048's sibling discussion below) silently disagrees with
  itself across replicas — a bug a single instance can never show you.
- Reuses infrastructure this day already introduced (Redis) instead of pulling forward Day 11's
  gateway early.

### Negative
- A second HTTP hop (nginx -> app) that single-instance dev doesn't have — negligible latency,
  but real infrastructure a student now has to reason about.
- 3x the database/Redis connection pool pressure from one host, compounding with the pool
  lessons on Day 5 (ADR-015) if a class runs the dinner-rush profile against `scale-out`.
- All 3 replicas run `db.Database.Migrate()` on startup (a Day-1 dev convenience); simultaneous
  first-boot migration attempts are possible. Acceptable for a local demo profile (`restart:
  unless-stopped` recovers); a real deployment runs migrations as a separate release step, not
  app-startup code — that discipline starts mattering for real at Day 12's zero-downtime migration.

### Risks
- Explicit replica services mean adding a 4th replica is a compose-file edit, not a flag. Accepted
  for a teaching demo capped at "prove the concept with 3"; production autoscaling is a different
  (cloud-native) problem, covered as a black box at Day 12.

## Alternatives Considered

### Option A: `docker compose --scale api=3` against one service definition
- Simpler compose file, no repeated blocks.
- Rejected for THIS demo: nginx needs to track known backend health, and a single dynamically
  resolved DNS name doesn't give it stable per-replica state to evict/readmit against — the
  killed-replica demo either doesn't work cleanly or needs extra nginx `resolver` plumbing that
  adds complexity without adding a lesson.

### Option B: Pull the Day 11 YARP gateway forward instead of nginx
- Rejected: Day 11's gateway is introduced when there are multiple SERVICES to route between
  (Payment, Delivery, Restaurant) — using it here, a week before any service exists, teaches the
  wrong trigger for "why a gateway." nginx here is doing exactly one job (load balance N
  replicas of ONE service), which is the Day 6 lesson.

## References
- ADR-016 (read replica — the other place "which backend answered" already mattered)
- `docker-compose.yml` (`scale-out` profile), `docker/nginx/lb.conf`, `src/Tadka.Api/Dockerfile`
- Break kit: `cohort-prep/day-06/break-kit-day-06.md`

## Revisit When
Day 11 introduces the YARP gateway once there are multiple services to route between — this
nginx LB is a Day 6-scoped teaching tool, not the production answer (that becomes the cloud
load balancer in front of the gateway, discussed black-box at Day 12).
