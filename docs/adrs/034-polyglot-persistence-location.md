# ADR-034: Polyglot Persistence for Live Location — Redis-geo + Postgres

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

The Delivery service (ADR-033) has two very different data needs:
1. **Live rider location** — written every few seconds per active agent, read sub-millisecond by the customer's tracking screen, and **only the latest value matters** (history of every ping is noise).
2. **Assignment + delivery history** — which agent took which order, timestamps, status — **durable**, queried occasionally, must survive restarts.

One store can't be ideal for both. Forcing live location into Postgres means a row insert/update per ping → WAL, index updates, a connection from the pool — at 200 pings/s that's table/index bloat and vacuum pressure for data you'll throw away.

## Decision

**Use the right store per workload (polyglot persistence):**
- **Redis-geo for live location.** `GEOADD delivery:agents <lon> <lat> <agentId>` overwrites the previous position (no growing table); `GEOPOS` reads it in sub-ms. **Assignment is first-available today** (`FirstOrDefault` on `Available`) — we do **not** call `GEOSEARCH`. Nearest-N / `GEOSEARCH` is the named next move, not the shipped code. Ephemeral by design — if Redis restarts, the next ping (within seconds) repopulates it.
- **Postgres for assignment + history.** `DeliveryAssignment` (order↔agent, status, timestamps) is durable, transactional, and survives restarts.

## Consequences

### Positive
- Location ingestion never touches the order-path Postgres; sub-ms reads/writes; no unbounded table.
- Durable data (assignments) keeps Postgres' guarantees.
- A textbook "right tool for the job" — the same instinct as Day-6 cache vs source-of-truth.

### Negative / Risks
- **Two stores for one service's data** + the discipline of knowing which is which. Live location is **not durable** — acceptable (ephemeral by nature), but must be a conscious choice, not an accident.
- If you later need location *history* (analytics), you must export from Redis on a schedule (Redis is not your analytics store).

### Cost (₹ / effort)
Reuses the existing Redis; the geo commands are built in. Near-zero added infra.

## Alternatives Considered
- **Postgres / PostGIS only:** real geo queries, but a write per ping → bloat/vacuum at scale; *failure mode:* a team stored every ping "for analytics" → 17M rows/day, nearest-agent latency 5ms→800ms. Kept Postgres for *history*, not live pings.
- **Redis only:** loses durability for assignments/financial-ish reconciliation. Rejected for the durable half.
- **A dedicated geospatial/time-series DB:** over-reach today; revisit if location *history* analytics become first-class.

## Cross-stack equivalents
Redis-geo (`GEOADD`/`GEOSEARCH`) is the same from any stack: StackExchange.Redis (.NET) · Lettuce/Jedis (Java) · ioredis (Node) · go-redis (Go). PostGIS is the Postgres-native geo alternative (any stack). The pattern — **ephemeral hot data in an in-memory geo store, durable data in the RDBMS** — is language-neutral polyglot persistence.

## References
- ADR-018 (Redis cache — the same "performance store vs source of truth" instinct), ADR-033 (Delivery extraction)
- `cohort-prep/day-11/break-kit-day-11.md` (GEOADD → GEOPOS live track), `option-space.md` (location-store matrix)
- Implementation: `src/Tadka.Delivery.Api` — Redis geo for location, `PaymentDbContext`-style Postgres for assignments

## Revisit When
Add a **Redis → time-series/analytics** export if delivery-time-by-zone analytics are needed. Reconsider PostGIS if you need complex polygon/route queries beyond proximity. Add a Redis **persistence/HA** posture if "lose live location on restart" ever becomes unacceptable (rare).
