# ADR-038: Zero-Downtime Migration — Expand & Contract + Online Data Backfill

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Extracting Restaurant (ADR-036) and standing up the Ordering read model (ADR-037) are not just code changes — they are **data** changes on a live system. Two real migrations hide inside Day 12:
1. **Move** Restaurant's rows from the monolith's `restaurant` schema to the new `restaurant-db`.
2. **Seed** the monolith's new `ordering.menu_replica` from the existing menu (the replica starts empty; events only carry *future* changes, so the *current* menu must be backfilled).

In a tutorial you'd stop the app, run `dotnet ef database update`, and restart. In production Tadka serves orders 24×7 — **downtime is not on the table**, and the hard part is rarely the schema (DDL is fast); it's the **data backfill**. Backfilling millions of rows naïvely will: take a long lock (`UPDATE`/`ALTER` rewrites block reads/writes), saturate disk IO, and **blow out replication lag** (ADR-016's replica falls behind → stale reads everywhere) — turning a "migration" into an outage.

A breaking schema change (rename/retype a column, split a table) has the same shape: you cannot flip old→new atomically while N app instances are running both versions of the code.

## Decision

Adopt **Expand & Contract** (a.k.a. parallel-change) for schema, and **chunked, throttled, online backfill** for data — both as the team's standard, documented here and demonstrated by seeding the read model.

**Expand & Contract (schema):**
1. **Expand** — add the new column/table; keep the old. Non-breaking.
2. **Dual-write** — application writes both old and new.
3. **Backfill** — copy historical rows old→new, online (below).
4. **Switch reads** — read from new; verify.
5. **Contract** — stop writing the old; drop it in a later, separate deploy.

Each step is its own deploy; at no point is there a version of the app that can't run against the current schema. (For Day 12 the column rename is taught as the canonical example; the *built* migration is the data backfill below.)

**Online data backfill (the demonstrated piece):**
- **Chunked**: process N rows per batch (e.g. 500–1000), commit each batch — never one giant transaction.
- **Claim with `FOR UPDATE SKIP LOCKED`**: the same primitive as the Outbox relay (ADR-028 / CTO review #2) — lets the backfill run in parallel workers and co-exist with live traffic without lock waits.
- **Throttled**: sleep between batches; **watch replication lag** (`pg_last_wal_replay_lag` / `pg_stat_replication`) and back off when it grows — protect the replica (ADR-016).
- **Idempotent + resumable**: upsert by key, track a high-water mark, so a crash resumes, not restarts.
- **Never lock the table**: no `ALTER … REWRITE` on a hot table; use additive DDL; use `pg_repack`/`CREATE INDEX CONCURRENTLY` analogs when a rewrite is unavoidable.

Tadka ships `scripts/backfill-menu-replica.ps1` that seeds `ordering.menu_replica` this way; after it runs, `menu-updated` events keep the replica fresh (ADR-037).

## Consequences

### Positive
- **No downtime, no table lock, reads never break** — order intake continues during the migration.
- The replica is backfilled safely; events take over for steady state.
- One reusable backfill discipline (chunk + SKIP LOCKED + throttle + resume) for every future large data change.

### Negative / Risks
- Multi-step and **slow by design** (throttling trades speed for safety) — a backfill can run for hours/days; you must monitor it.
- Dual-write windows add temporary code complexity that you must remember to **contract** (dead schema rots if step 5 is skipped).
- Getting throttling wrong (too aggressive) still risks replication lag — must watch the gauge, not guess.

### Cost (₹ / effort)
Engineering time + a longer migration window; **far cheaper than the outage** a naive `UPDATE … SET` on a 50M-row hot table would cause (lock + lag → failed orders during dinner rush).

## Alternatives Considered

### Option A: Stop-the-world migration (maintenance window)
- Pros: simplest; no dual-write.
- Cons: downtime; impossible at 24×7 dinner-rush scale; long lock on big tables.
- Why rejected: downtime is off the table.

### Option B: One big `UPDATE`/`ALTER` online, no chunking
- Pros: one statement.
- Cons: long lock, IO saturation, replication-lag blowout → stale reads / failed writes everywhere.
- Why rejected: this *is* the outage we're avoiding.

### Option C: CDC (Debezium) to stream the backfill
- Pros: continuous, handles ongoing changes natively; great for very large/continuous migrations.
- Cons: a whole pipeline (Kafka Connect + Debezium) to operate for a one-time seed.
- Why rejected *for now*: overkill for seeding one replica; **named as the production tool** when the data volume/continuity justifies it (and it pairs with the Outbox/Kafka we already run).

## Teaching fields

- **Topic:** Changing schema **and** data on a live system without downtime — Expand & Contract for schema, chunked/throttled/SKIP-LOCKED backfill for data, and why **the data backfill (not the DDL) is the hard part**.
- **Options:** stop-the-world · big online UPDATE · **expand-contract + chunked throttled backfill (chosen)** · CDC (named for scale).
- **Choice:** parallel-change schema steps; backfill in batches claimed via `FOR UPDATE SKIP LOCKED`, throttled against replication lag, idempotent + resumable.
- **Why:** zero downtime, no long locks, protects the read replica, resumable on crash.
- **Trade-off:** many steps + a slow, monitored run; you must finish the *contract* step or carry dead schema.
- **Failure mode** (2 AM dinner rush): an un-throttled backfill saturates the primary's IO and pushes the streaming replica's lag from 200 ms → 40 s → every replica read is stale, read-your-writes breaks (ADR-016), customers see "order not found" right after placing it. Throttle + lag-watch + back-off prevents it.
- **Revisit when:** data volume/continuity outgrows a scripted batch job → move to **CDC (Debezium)** or a managed online-DDL tool (gh-ost/pt-osc) feeding the Outbox/Kafka backbone.
- **Cross-stack equivalents:** Expand & Contract is universal (Flyway/Liquibase migrations in Java; Prisma Migrate/Knex in Node; golang-migrate/goose in Go — all do additive steps). Online backfill: gh-ost / pt-online-schema-change (MySQL), `pg_repack` + `CREATE INDEX CONCURRENTLY` (Postgres), batched jobs everywhere; `SELECT … FOR UPDATE SKIP LOCKED` exists in MySQL 8+ and Postgres alike. CDC ≈ Debezium / Maxwell.

## References
- ADR-016 (read replica + lag — what backfill must protect), ADR-028 / CTO review #2 (`FOR UPDATE SKIP LOCKED`, the same claim primitive), ADR-036/037 (the two migrations this enables — move Restaurant data + seed the replica)
- `cohort-prep/day-12/option-space.md` (migration-strategy matrix), `break-kit-day-12.md` (captured backfill run + lag)
- Implementation: `scripts/backfill-menu-replica.ps1`

## Revisit When
When a single backfill must run continuously or against tens of millions of rows during peak, graduate from the scripted batch job to **CDC (Debezium → Kafka)** so the migration is a stream, not a sweep — reusing the Day-9 backbone.
