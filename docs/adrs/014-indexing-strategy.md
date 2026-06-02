# ADR-014: Indexing Strategy — Pre-Plan by Query Pattern, Validate with EXPLAIN

**Date:** 2026-06-02
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

As Tadka grows past its Bangalore launch (1 lakh orders/day) toward 3–4 cities (~4 lakh/day), the `orders` table accumulates rows fast. During the dinner rush, the order-history query `WHERE customer_id = ? ORDER BY created_at DESC` runs a **sequential scan** — Postgres reads every row, every time. At a few hundred rows nobody notices; at a few hundred thousand it takes seconds, and under concurrency those slow queries hold connections open long enough to exhaust the pool (ADR-015). We saw it: `EXPLAIN ANALYZE` shows `Seq Scan`, and the k6 dinner-rush profile blows the p99 < 300ms NFR.

We need indexes — but indexes are not free. Every index is updated on every write and consumes disk and memory. The discipline is to add indexes **earned by a real query pattern**, not one per column "just in case."

## Decision

**Pre-plan indexes from the known query patterns (the API endpoints), then validate each with `EXPLAIN ANALYZE` on a realistically large table.** For `orders`:

- **`(customer_id, created_at DESC)`** composite — serves `GET /orders?customerId=…` ordered by recency (the order-history page). The composite lets Postgres satisfy both the filter and the sort from the index (no separate `Sort` node).
- **`(created_at DESC)`** — serves `GET /orders` (all, recency-ordered).

`order_items(order_id)` is already indexed automatically (EF creates indexes on FK columns). `customer_id`/`created_at` are bare values (no cross-schema FK, ADR-008), so they get **no** automatic index — these are genuinely needed.

**Deliberately NOT added:** `orders(status)` and `orders(restaurant_id)` — no endpoint filters on them today, so an index would only tax writes. We add them the day a query needs them. Naming what we *don't* index is as much the decision as what we do.

## Consequences

### Positive
- Order-history and recency queries go from `Seq Scan` (O(n)) to `Index Scan`/`Index Only Scan` — a 100×+ improvement on a large table, provable with `EXPLAIN ANALYZE` before/after.
- The composite also removes the in-memory `Sort`, the hidden cost people miss.
- A small, intentional index set keeps write amplification low.

### Negative / Risks
- Two more indexes to maintain on every `orders` write. Acceptable: writes are ~1.2/s avg, and the read win is large.
- An index the planner doesn't use is pure cost. **Mitigation:** validate with `EXPLAIN`; periodically check `pg_stat_user_indexes` for `idx_scan = 0` (zombie indexes) and drop them.

### Cost (₹ / effort)
Near-zero: a code-first migration (`AddPerformanceIndexes`) and a few minutes. The expensive alternative — reaching for a read replica or a bigger box while a `CREATE INDEX` was the real fix — is what this ADR prevents.

## Alternatives Considered
- **Index every foreign key / column "just in case"** — write throughput collapses (a real startup saw −40% INSERT after 12 indexes, 3 used). Rejected.
- **Wait for production to complain** — your first thousand users eat slow queries; reactive firefighting. Rejected (we pre-plan from known patterns).
- **Materialized views / denormalized read tables** — heavier; reserved for queries an index genuinely can't serve. Not needed at this scale.

## References
- ADR-008 (no cross-schema FKs — why these columns have no auto-index), ADR-015 (the pool exhaustion the seq scans triggered), ADR-012 (xmin)
- `docs/database/indexing-strategy.md`, `docs/scaling-decision-tree.md`, `docs/demo-scripts/01-explain-orders-by-customer.sql`
- `Data/Configurations/OrderConfiguration.cs`, `Migrations/*AddPerformanceIndexes*`

## Revisit When
When a **new query pattern** appears (then add the index it needs — e.g. a restaurant dashboard would justify `orders(restaurant_id, created_at)`), or when `pg_stat_user_indexes` shows an index with `idx_scan = 0` (drop it), or when an index-optimized query still shows >100ms p95 — at which point caching (Day 6) or partitioning (ADR-017) is the next cheapest move, not more indexes.
