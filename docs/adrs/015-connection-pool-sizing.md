# ADR-015: Connection Pool Sizing (tuned Npgsql pool; PgBouncer deferred)

**Date:** 2026-06-02
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

The dinner-rush incident is not a *volume* problem — 1 lakh orders/day is ~1.2 writes/s. It's a *concurrency* problem. At 8 PM, hundreds of users hit the hot read paths at once. Each request borrows a database connection from Npgsql's pool for the duration of its query. When a few queries are slow (a missing index, ADR-014) or simply many arrive together, connections stay checked out, the pool drains, and **new requests queue waiting for a free connection** — then time out. p99 explodes for *every* endpoint, even ones that were never slow. The causal chain is: `concurrency × hold-time × pool-size`.

We need a pool sized to the workload — large enough to absorb the rush, small enough that multiple app instances don't blow past Postgres's `max_connections` (default 100, ~10 MB RAM each).

## Decision

**Tune the Npgsql pool explicitly in the connection string** (`Minimum Pool Size=5; Maximum Pool Size=50` for dev), and **defer PgBouncer** until we run multiple app instances behind a load balancer (Day 11). Rule of thumb: pool ≈ the number of *concurrent DB operations* you actually need, bounded so `instances × MaxPoolSize ≤ Postgres max_connections` with headroom.

The dinner-rush demo deliberately shrinks the pool to `Maximum Pool Size=10` to make exhaustion happen in 30 seconds; the committed value is a sane 50.

## Consequences

### Positive
- The rush is absorbed without queueing once the pool matches the workload (and once the slow queries are indexed — the two fixes compound).
- A bounded pool keeps total connections predictable as we scale horizontally later.

### Negative / Risks
- A pool that's too *large* per instance × many instances → Postgres connection/RAM exhaustion. **Mitigation:** the `instances × MaxPoolSize` budget, and PgBouncer when instances multiply.
- A pool that's too *small* re-creates the queueing. **Mitigation:** size from measured concurrent demand, not a guess.
- Bigger pool alone does **not** fix a slow query — it just delays exhaustion; you'd need an ever-bigger pool. Indexes (ADR-014) fix the root cause.

### Cost (₹ / effort)
Zero infra — a connection-string setting. PgBouncer (deferred) is one more process to run/monitor; we pay that only when multiple instances make it necessary.

## Alternatives Considered
- **Leave the default (100)** — hides the problem on a laptop, then 4 instances × 100 = 400 connections crush a default-configured Postgres. Rejected.
- **PgBouncer now** — multiplexes many logical connections onto few physical ones, but it's operational overhead (another hop, config, monitoring) for a single-instance app. Rejected now; **revisit at Day 11**.
- **AWS RDS Proxy** — managed pooling; relevant once we're on RDS + ECS (Week 6), not local dev.

## References
- ADR-014 (the slow query that drained the pool), ADR-002 (monolith-first)
- `docs/database/connection-pooling-guide.md`, `docs/break-kits/week-02.md`, `docs/demo-scripts/02-pgbouncer-connection-exhaustion.ps1`
- `src/Tadka.Api/appsettings.Development.json`

## Revisit When
When we run **multiple app instances** (Day 11, ECS) and `instances × MaxPoolSize` approaches Postgres `max_connections` → introduce **PgBouncer/RDS Proxy**. Also revisit the size itself if p99 degrades while pool-wait time is near zero — that means the bottleneck moved to the DB (CPU/IO), not the pool.
