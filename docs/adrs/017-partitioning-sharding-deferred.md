# ADR-017: Partitioning & Sharding Deferred — Cheapest-First, Triggered by Measurement

**Date:** 2026-06-02 (evidence added 2026-07-12)
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Scaling has a natural order-of-magnitude cost ladder: **indexes (free) → caching (cheap) → read replica (a box) → partitioning (a schema migration) → sharding (an architecture rewrite)**. On Day 5 students learn partitioning and sharding *as concepts* and immediately want to apply them. They shouldn't yet. At ~4 lakh/day the `orders` table is comfortably served by a B-tree index; partitioning and sharding solve problems Tadka does not have. The decision worth recording is **when** they become justified — and why reaching for them now is a mistake.

## Decision

**Do not partition or shard now. Teach both, demo them on throwaway tables, and define measurable triggers.**

- **Range-partition `orders` by `created_at`** *when* the table exceeds ~1 crore rows (≈3 years at the launch rate, sooner with growth) **or** when an index-optimized query still shows >100ms p95 because the working set no longer fits in RAM. Partition pruning then gives a step-change indexes can't.
- **Shard across multiple databases** *only* when a single primary hits its **write ceiling** (not read — that's the replica's job). This is the Week-8 / "Instagram scale" conversation, not now.
- Cheapest-first is the universal rule: apply the next rung only when measurement proves the current one is exhausted.

## Evidence (Day 5, Beat 4, captured on the 200k-row seed)

The "partition pruning is a win, partitioning by a mutable key is a trap" claim above was
previously conceptual (3 dummy rows in `docs/demo-scripts/03-table-partitioning.sql`). It is now
backed by `scripts/day05-partition-demo.sql` run against the real 200k-row seed:

- **Pruning wins:** an unfiltered query on a `created_at`-partitioned copy scans every partition
  (**~12.5 ms**); the same query with a one-month date filter scans exactly one partition
  (`Subplans Removed: 7` in the plan, **~6.95 ms**) — pruning is real and visible, even at this
  modest scale, and the win compounds with more/larger partitions.
- **The mutable-key trap is real, not theoretical:** on a `status`-partitioned copy, an identical
  5,000-row bulk UPDATE costs **54 dirtied/written buffers** when it stays in one partition
  (`total_amount` only) versus **88/89 (+63%)** when the update also changes `status` and the row
  must move partitions (delete from the old partition, insert into the new one) — plus +5.2% WAL
  bytes. `orders.status` changes on every single order lifecycle (`Created → … → Delivered`),
  which is exactly why this ADR partitions by `created_at`, never by `status`.

This is why the Decision below says "partition by `created_at`" specifically, not "partition
`orders`" — the column choice is not interchangeable, and getting it wrong makes every status
transition (i.e. most of the app's write traffic) permanently more expensive.

## Consequences

### Positive
- Avoids the real costs of premature partitioning: the partition key must be in the primary key (`orders` PK becomes `(id, created_at)`), which complicates every lookup-by-id and FK, and tooling (`pg_dump`, EF migrations) gets fiddlier.
- Avoids sharding's distributed-query/cross-shard-join complexity entirely until forced.
- Students leave able to *defend the sequence* — the architect skill the capstone tests ("at what number did each move become necessary?").

### Negative / Risks
- A future migration to a partitioned table is real work — but it's a migration, not a rewrite, and the triggers give early warning.
- Teaching a tool you don't deploy risks "we learned it, let's use it" enthusiasm. **Mitigation:** the explicit triggers + the cheapest-first decision tree.

### Cost (₹ / effort)
Zero now (concepts + demo SQL on throwaway tables). The point is to *not* spend the migration/rewrite cost until the numbers demand it.

## Alternatives Considered
- **Partition `orders` from day one** — makes simple `WHERE id = ?` lookups scan all partitions unless they also specify `created_at`; you've made the common case harder to optimize a problem you don't have. Rejected.
- **Shard early "to be safe"** — resume-driven over-engineering; introduces distributed transactions and cross-shard queries for ~1.2 writes/s. Rejected.
- **Archive/TTL old data instead of partitioning** — a complementary, even cheaper move for cold data; consider it *before* sharding when the issue is table size, not write rate.

## References
- ADR-014 (indexes — the rung below), ADR-016 (read replica — the rung between), ADR-046 (keyset
  pagination — a cheaper fix than partitioning for the specific "deep page" symptom)
- `docs/scaling-decision-tree.md`, `docs/database/instagram-sharding-case-study.md` (Week 8), `docs/demo-scripts/{03-table-partitioning,04-sharding-concept,05-instagram-id-generation}.sql`
- `scripts/day05-partition-demo.sql` — reproduces the captured evidence above at real (200k-row) scale
- `docs/tadka-growth-story.md` (Week 8 = the scale ceiling)

## Revisit When
`orders` approaches **~1 crore rows** or index-optimized queries exceed **>100ms p95** from RAM pressure → **partition by date**. A single primary approaches its **write ceiling** despite partitioning → evaluate **sharding** (Week 8, Instagram case study). Never reach for either before the measurement says so.
