# ADR-015: Connection Pool Sizing (tuned Npgsql pool; PgBouncer landed on Day 11)

**Date:** 2026-06-02 (PgBouncer landed 2026-09-08, Day 11)
**Status:** Accepted — PgBouncer landed
**Deciders:** Tadka Engineering Team

## Context

The dinner-rush incident is not a *volume* problem — 1 lakh orders/day is ~1.2 writes/s. It's a *concurrency* problem. At 8 PM, hundreds of users hit the hot read paths at once. Each request borrows a database connection from Npgsql's pool for the duration of its query. When a few queries are slow (a missing index, ADR-014) or simply many arrive together, connections stay checked out, the pool drains, and **new requests queue waiting for a free connection** — then time out. p99 explodes for *every* endpoint, even ones that were never slow. The causal chain is: `concurrency × hold-time × pool-size`.

We need a pool sized to the workload — large enough to absorb the rush, small enough that multiple app instances don't blow past Postgres's `max_connections` (default 100, ~10 MB RAM each).

## Decision

**Tune the Npgsql pool explicitly in the connection string** (`Minimum Pool Size=5; Maximum Pool Size=50` for dev). PgBouncer was deferred until we actually run multiple app instances — **that condition landed on Day 11** (`Tadka.Delivery.Api` + `Tadka.Gateway` mean the compose stack now runs more than one process), so a `pgbouncer` service (transaction-mode pooling, `:6432`, `default_pool_size=20`) is now part of `docker-compose.yml`. Rule of thumb: pool ≈ the number of *concurrent DB operations* you actually need, bounded so `instances × MaxPoolSize ≤ Postgres max_connections` with headroom; once you can't hold that budget (more instances than headroom allows), put a pooler in front instead of shrinking `MaxPoolSize` further.

The dinner-rush demo deliberately shrinks the pool to `Maximum Pool Size=10` to make exhaustion happen in 30 seconds; the committed value is a sane 50.

### Day-11 update: the multi-instance demo, with real captured numbers

Two `Tadka.Api` instances (`Maximum Pool Size=60` each, so `2 × 60 = 120` client-side capacity against Postgres's default `max_connections=100`), 300 concurrent requests to `GET /api/v1/restaurants` split evenly across both:

| | Direct to Postgres `:5432` | Via PgBouncer `:6432` |
|---|---|---|
| Successful | 296 / 300 | 300 / 300 |
| Failed | **4 / 300** — `Npgsql.NpgsqlException`: *"sorry, too many clients already"* (Postgres's own connection-limit error, surfaced as an unhandled 500) | **0 / 300** (repeated twice, both clean) |
| Wall clock | 1829 ms | 1591 ms (cold), 510 ms (warm repeat) |
| p99 (successful) | 807 ms | 731 ms (cold), 81 ms (warm repeat) |
| Physical backend connections (`pg_stat_activity`, user `tadka`) | up to ~120 possible (2 × 60 pool ceiling) | **14–16**, confirmed via `SHOW POOLS;` (`sv_idle=14`) against `cl_active=110` client-side connections |

The failure rate direct-to-Postgres is modest (~1.3%) — this is an honest number from a real run, not inflated for effect; the qualitative story is what matters: **zero errors through PgBouncer vs. real Postgres-level connection refusals without it**, plus the `SHOW POOLS` proof that 110+ client-side pooled connections were multiplexed onto ~14-16 real backend connections. Reproduce with `docs/demo-scripts/02-pgbouncer-connection-exhaustion.ps1` (see Day-11 runbook).

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
- **PgBouncer now (Day 5)** — multiplexes many logical connections onto few physical ones, but it's operational overhead (another hop, config, monitoring) for a single-instance app. Rejected then; **landed at Day 11** once a second instance existed to actually contend for connections.
- **AWS RDS Proxy** — managed pooling; relevant once we're on RDS + ECS (Week 6), not local dev.

## References
- ADR-014 (the slow query that drained the pool), ADR-002 (monolith-first)
- `docs/database/connection-pooling-guide.md`, `docs/break-kits/week-02.md`, `docs/demo-scripts/02-pgbouncer-connection-exhaustion.ps1`
- `src/Tadka.Api/appsettings.Development.json`

## Revisit When
**Landed on Day 11** — PgBouncer is in `docker-compose.yml` and the multi-instance before/after is captured above. Next revisit: moving to RDS Proxy or a cloud-managed pooler once ECS deployment (Day 12/ADR-039) makes horizontal scaling routine, not a demo. Also revisit the pool size itself if p99 degrades while pool-wait time is near zero — that means the bottleneck moved to the DB (CPU/IO), not the pool.
