# Day 5 — Runbook: indexes, the pool, and a real read replica

**Branch:** `day-05`. **What's new:** performance indexes (ADR-014), a tuned connection pool (ADR-015), and a **real streaming read replica** with an EF read/write split (ADR-016). Partitioning is a standalone SQL experiment (ADR-017), not an app change.

> **Windows PowerShell:** use `curl.exe`, not `curl`.

| Thing | Value |
|-------|--------|
| API | `http://localhost:5224` |
| Compose service | `postgres` (container `tadka-postgres`) |
| **Primary** | `localhost:5432`, db/user `tadka`, password `tadka_local` |
| **Replica** | `localhost:5433` (container `tadka-postgres-replica`) |

**Two Postgres containers today.** Run `docker compose down -v` when switching onto this branch, otherwise the replica initialises against a stale volume.

Seed GUIDs (same as Day 3): Meghana `a1b2c3d4-0001-4000-8000-000000000001`, biryani `b1b2c3d4-0001-4000-8000-000000000001`, Priya `c1b2c3d4-0001-4000-8000-000000000001`.

---

## 0. Fresh start (pre-class)

```bash
git checkout day-05
docker compose down -v
docker compose up -d
docker compose ps
```

Wait until **both** `tadka-postgres` and `tadka-postgres-replica` report healthy. Then:

```bash
dotnet build Tadka.slnx
dotnet run --project src/Tadka.Api
```

---

## 1. Seed 200k orders

The break is invisible on 16 rows. This is what makes `EXPLAIN` bite.

```bash
docker cp scripts/day05-seed-large.sql tadka-postgres:/tmp/seed.sql
docker exec -i tadka-postgres psql -U tadka -d tadka -f /tmp/seed.sql
```

Verify, and expect roughly 200,001:

```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT count(*) FROM ordering.orders;"
```

Run this against the **primary** (5432). It streams to the replica over WAL.

---

## 2. Beat 1 — induce the seq scan, then fix it

**Break it:**

```bash
docker cp scripts/day05-induce-break.sql tadka-postgres:/tmp/break.sql
docker exec -i tadka-postgres psql -U tadka -d tadka -f /tmp/break.sql
```

The script drops the performance indexes and runs the order-history `EXPLAIN ANALYZE`. Look for **`Parallel Seq Scan`** plus a **`Sort`** node, and read the execution time out loud.

**Fix it:**

```bash
docker cp scripts/day05-apply-fix.sql tadka-postgres:/tmp/fix.sql
docker exec -i tadka-postgres psql -U tadka -d tadka -f /tmp/fix.sql
```

Re-run the same `EXPLAIN`. Two things change, and both are the lesson:

- `Parallel Seq Scan` becomes **`Index Scan`** (roughly 20× on a dev laptop)
- the **`Sort` node disappears entirely**, because the index is already in `created_at DESC` order

> **The ratio is the lesson, not the milliseconds.** Say that to the room once, early. Your absolute numbers will differ from mine.

---

## 3. Beat 2 — the pool

Show that the connection count is a **memory budget**, not a preference:

```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "SHOW max_connections;"
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT count(*) FROM pg_stat_activity WHERE datname='tadka';"
```

Each of those rows is a **separate OS process** on the server at roughly 1–3 MB, which is the whole point of `[S3-B1a]`. Prove it:

```bash
docker exec tadka-postgres ps -o pid,rss,cmd -C postgres | head
```

Then squeeze the pool (connection string `Maximum Pool Size`) and put load on it while watching:

```bash
docker stats tadka-postgres --no-stream
```

> **Honesty note for the room:** one laptop cannot truly exhaust a well-sized pool. The formula is `concurrency × hold-time`, and the real exhaustion demo is Day 7's payment brownout. Say so rather than faking it.

---

## 4. Beat 3 — the replica is real

Confirm replication is actually streaming, not simulated:

```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT client_addr, state, sync_state FROM pg_stat_replication;"
docker exec tadka-postgres-replica psql -U tadka -d tadka -c "SELECT pg_is_in_recovery();"
```

`pg_is_in_recovery()` must return `t` on the replica. Now **CAP you can feel** — write to the primary, read the replica immediately:

```bash
curl.exe -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d '{ "customerId":"c1b2c3d4-0001-4000-8000-000000000001", "restaurantId":"a1b2c3d4-0001-4000-8000-000000000001", "items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":1}], "deliveryAddress":{"line1":"1","line2":"2","city":"Bangalore","pincode":"560066","latitude":12.97,"longitude":77.75} }'

docker exec tadka-postgres-replica psql -U tadka -d tadka -c "SELECT count(*) FROM ordering.orders WHERE customer_id='c1b2c3d4-0001-4000-8000-000000000001';"
```

Run the replica query **immediately** and the new order is not there yet. Measure the lag:

```bash
docker exec tadka-postgres-replica psql -U tadka -d tadka -c "SELECT now() - pg_last_xact_replay_timestamp() AS lag;"
```

That gap is the reason **read-your-writes goes to the primary**. It is ADR-016's failure mode, written down before it was ever hit.

### Replica is not a backup

The `[S4-B6a]` beat. Take one backup so the room has touched it:

```bash
docker exec tadka-postgres pg_dump -U tadka -d tadka -f /tmp/tadka-backup.sql
docker exec tadka-postgres ls -lh /tmp/tadka-backup.sql
```

A `DELETE` on the primary reaches the replica in milliseconds. **A replica gives you another machine; a backup gives you another point in time.** RPO is how much data you can lose, RTO is how long recovery takes.

---

## 5. Partitioning — standalone experiment

Nothing about the running app changes here. Separate tables, both directions shown.

```bash
docker cp scripts/day05-partition-demo.sql tadka-postgres:/tmp/partition-demo.sql
docker exec -i tadka-postgres psql -U tadka -d tadka -f /tmp/partition-demo.sql
```

Two results to read out:

- **The win** — partition on the immutable `created_at`, and a date-filtered query reports **`Subplans Removed`**. That is partition pruning.
- **The trap** — partition on the mutable `status`, and every status change becomes a `DELETE` + `INSERT` across partitions, with visibly higher buffer counts.

**Partitioning is not wrong. Partitioning on a column that changes is wrong.**

### Sharding — read, do not run

Sharding is board-only in class (`[S5-B5]`/`[S5-B6]`). The depth is already on this branch:

- [`docs/database/instagram-sharding-case-study.md`](../database/instagram-sharding-case-study.md)
- [`docs/demo-scripts/04-sharding-concept.sql`](../demo-scripts/04-sharding-concept.sql)

The one number to carry: `hash % N` moves about **80%** of your keys when you add a server; consistent hashing with virtual nodes moves about **1/N**.

---

## 6. Deep pagination

```bash
curl.exe -s "http://localhost:5224/api/v1/orders?customerId=c1b2c3d4-0001-4000-8000-000000000001&offset=3990&limit=10" -w "\n%{time_total}s\n"
```

Compare against the keyset/cursor endpoint. `OFFSET` makes the database *walk and discard* every skipped row; a cursor seeks straight into the index. On the seeded data that is roughly **74×**.

---

## 7. Reset between runs

```bash
docker compose down -v && docker compose up -d
```

Then re-run step 1. Re-seeding takes a couple of minutes, so do it **before** class, not during it.
