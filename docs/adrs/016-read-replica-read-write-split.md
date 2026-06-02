# ADR-016: Read Replica + Application-Level Read/Write Split (with a read-your-writes policy)

**Date:** 2026-06-02
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

With indexes (ADR-014) and a tuned pool (ADR-015), the dinner rush at one city is fine. But Tadka expands to 3–4 cities (~4 lakh/day) and the **read:write ratio widens** — people browse far more than they order. Even with caching coming later, the long tail of *unique* reads (different restaurants, filters, order-history pages) lands on the primary and pins its CPU, and writes start to suffer because reads are stealing cycles. The bottleneck is now **read volume on a single instance**, which indexes and pooling can't solve.

## Decision

**Add a PostgreSQL streaming read replica and route read-heavy GETs to it at the application level**, keeping all writes — and reads that must be fresh — on the primary.

- **Infra:** a second Postgres (`tadka-postgres-replica`, port 5433) that clones the primary via `pg_basebackup` and follows it by streaming WAL (`wal_level=replica`). Only the primary is migrated; the schema reaches the replica through replication.
- **App:** a `TadkaReadDbContext` (NoTracking) bound to the replica connection. Read-heavy GETs that tolerate slight staleness — **restaurant list, menu, order history** — use it. Everything else uses the primary `TadkaDbContext`.
- **The consistency rule (the crux):** a read that follows a user's own write within the same flow must hit the **primary**. Concretely, `GET /orders/{id}` (the order you *just placed*) stays on the primary; `GET /orders?customerId=` (history) goes to the replica. This is **read-your-writes** consistency, chosen per query.

We route in the **application**, not a proxy, because only the developer knows which GET tolerates stale data and which needs read-your-writes — a proxy can't tell them apart.

## Consequences

### Positive
- Read load moves off the primary; the primary's CPU is freed for writes and fresh reads.
- **CAP becomes something you can feel, not a slide:** place an order, immediately read it from the replica, and watch it 404 for a few milliseconds until replication catches up. That demo motivates the consistency rule.
- Application-level routing gives explicit, per-query control (`_read.Orders` vs the primary repository).

### Negative / Risks
- **Replication lag** → a misrouted read-your-writes shows "order not found" right after placing → a support ticket. **Mitigation:** the rule above, enforced in review: *any GET that can follow a user-initiated write in the same flow uses the primary.*
- More infra to run and monitor (a second Postgres, replication health, lag). **Mitigation:** healthchecks; lag alerting arrives with observability (Day 13).
- Two contexts = developer must consciously pick. That cognitive cost is the point — wrong automatic routing is worse.

### Cost (₹ / effort)
One more Postgres instance (~one box) + a NoTracking context + routing on a handful of GETs. Cheaper than sharding or extraction, and it's the right rung on the cheapest-first ladder once read *volume* (not a missing index) is the proven bottleneck.

## Alternatives Considered
- **Proxy-level routing (PgBouncer/Pgpool)** — can't distinguish stale-tolerant GETs from read-your-writes GETs; would 404 freshly-placed orders. Rejected.
- **Scale the primary vertically** — buys time, costs forever, and doesn't address read/write contention. Rejected (running out of room is the whole premise).
- **Cache first (Redis)** — absorbs *repeated* reads (that's Day 6), but not the unique-read long tail saturating the primary. Replica and cache solve different halves; both are earned.

## References
- ADR-004 (Postgres), ADR-014 (indexes — done first), ADR-015 (pool), ADR-008 (no cross-schema FKs)
- `docker-compose.yml` + `docker/{primary-init,replica-entrypoint}.sh`, `Data/TadkaReadDbContext.cs`, `Controllers/{Restaurants,Orders}Controller.cs`
- `docs/scaling-decision-tree.md`

## Revisit When
At **service extraction (Week 6)**, read/write routing becomes per-service (each service owns its database), not per-context. Revisit the consistency policy if **replication lag exceeds ~2s** (then more reads must go to the primary, or use synchronous replication for those), or if the read:write ratio narrows enough that the replica is no longer worth its operational cost.
