# ADR-038: Zero-Downtime Migration Strategies

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Architectural changes, such as extracting the Restaurant service (ADR-036) and building a Local Read Model for Ordering (ADR-037), require migrating massive amounts of data on a live, highly available system. 

For example, the new `ordering.menu_replica` table must be seeded with millions of existing menu rows. Executing a naive, monolithic `INSERT` or `UPDATE` script on a production database will acquire long-running exclusive locks, block incoming transactional traffic, saturate disk I/O, and cause catastrophic replication lag on read replicas. This turns a migration into a system outage. We require a standardized approach to schema and data migrations that guarantees zero downtime.

## Decision

We will adopt the **Expand & Contract** pattern for schema changes, and **Chunked, Throttled, Online Backfills** for data migrations.

1. **Expand & Contract (Parallel Change) for Schema:**
   - *Expand:* Add the new schema elements (tables/columns) without removing or altering the old ones.
   - *Dual-Write:* Update application code to write to both the old and new schema elements simultaneously.
   - *Backfill:* Copy historical data from the old structure to the new structure.
   - *Switch Reads:* Cut over application read paths to use the new structure.
   - *Contract:* Remove the dual-write logic and safely drop the old schema elements in a subsequent deployment.

2. **Online Data Backfill (For Data Seeding):**
   - *Chunked Processing:* Data is migrated in small, discrete batches (e.g., 500-1000 rows per transaction) to avoid long table locks and transaction log bloat.
   - *Concurrency Control:* Background workers use `FOR UPDATE SKIP LOCKED` to safely claim batches of rows without blocking live traffic or other workers.
   - *Throttling:* Migration scripts dynamically monitor database health (specifically `pg_stat_replication` for replica lag) and inject artificial pauses between batches to prevent I/O saturation.
   - *Idempotency:* Backfill batches are applied as upserts (`INSERT ... ON CONFLICT DO UPDATE`), so re-processing an already-applied row is a no-op, not a duplicate or a corruption. The shipped demo (`scripts/backfill-menu-replica.ps1`) tracks its keyset cursor in an in-memory script variable, not a persisted checkpoint — a crash mid-run loses the cursor, and a restart re-scans from the beginning rather than resuming past what was already applied. That is **safe** (idempotent upserts make redoing already-done batches harmless) but **not an efficient resume** — a genuinely large backfill that crashes near the end would redo the full pass. A persisted high-water mark (a small "last id processed" row/table, updated per batch) is the natural next step if backfills grow large enough for that inefficiency to matter.

## Consequences

### Positive
- **Zero Downtime:** Schema migrations and heavy data copying occur without taking the application offline or failing customer requests.
- **System Stability:** Throttling protects read replicas and overall database performance during the migration window.
- **Safety:** The parallel change pattern allows rapid rollback if the new schema or data logic proves flawed before the final contract phase.

### Negative / Risks
- **Operational Complexity:** Migrations become multi-step processes spanning several deployments, rather than a single script execution.
- **Extended Migration Windows:** Throttled backfills can take days to complete for very large datasets, requiring active monitoring.
- **Technical Debt Risk:** If teams forget to execute the final "Contract" phase, the system accumulates abandoned columns, tables, and dual-write logic.

## Alternatives Considered
- **Stop-The-World Maintenance Windows:** Rejected. Unacceptable for a 24/7 revenue-generating e-commerce platform.
- **Monolithic Online Updates:** Executing a single massive `UPDATE` or `INSERT INTO...SELECT` statement was rejected due to lock contention and replication lag blowouts.
- **Change Data Capture (CDC):** Tools like Debezium are highly effective for streaming migrations but were deemed overly complex for simple one-off backfills. 

## Revisit When
When data volumes exceed hundreds of millions of rows, or when we require continuous real-time data replication to external analytical stores, we will graduate from scripted batch jobs to formal Change Data Capture (CDC) pipelines utilizing Debezium and Kafka.
