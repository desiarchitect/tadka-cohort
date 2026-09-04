# Day 5 — Runbook: indexes, pool, replica, leftover events, history cursor

**Branch:** `day-05` (this file is long on purpose — Beat 1–6 each say command / what it does / fail check / fix check). If you only see a short “induce-break / look for Seq Scan” block, `git fetch origin; git checkout day-05; git reset --hard origin/day-05`.

**What's new:** performance indexes (ADR-014), Npgsql pool Min 5 / Max 50 (ADR-015), streaming replica `:5433` + `TadkaReadDbContext` (ADR-016), partition **SQL experiment** (ADR-017), `GET /api/v1/orders/history` (ADR-046). Day 4's `OrderConfirmed` handler **stays**. **No Redis.**

> **Windows PowerShell:** use **`curl.exe`**. From the repo root. Quote `@file`. Do not paste `-d "{...}"`.

| Thing | Value |
|-------|--------|
| API | `http://localhost:5224` |
| Primary | `localhost:5432` · container `tadka-postgres` · service `postgres` |
| Replica | `localhost:5433` · container `tadka-postgres-replica` |
| Creds | db/user `tadka`, password `tadka_local` |

**Two Postgres today.** `down -v` when switching onto this branch, or the replica clones a stale volume.

Seed GUIDs (menu / Priya — **not** the 200k history customer): Meghana `a1b2c3d4-0001-4000-8000-000000000001`, biryani `b1b2c3d4-0001-4000-8000-000000000001`, Priya `c1b2c3d4-0001-4000-8000-000000000001`.

**200k seed customer** (order history / load): `00000000-0000-0000-0000-000000000000`.

### Demo → code

| When | What you run | Look for | Code |
|---|---|---|---|
| **Opening — leftover SMS** | New order, PATCH Confirmed | HTTP **204** + **`dotnet run`** line `Notification: order … confirmed` | `Order.cs` 81–84 Raise; `OrdersController.cs` 231 then 233; handler 16–21 |
| Beat 1 — seq scan | `day05-induce-break.sql` (**prints** EXPLAIN) | **Seq Scan** or **Parallel Seq Scan** + **Sort** | Drops the two history indexes |
| Beat 1 — fix | `day05-apply-fix.sql` (**prints** EXPLAIN) | **Index Scan** using `ix_orders_customer_id_created_at`, **no Sort** | Recreates indexes; `OrderConfiguration.cs` 57–62 |
| Beat 2 — pool | two `measure-load.ps1` runs (baseline vs seq-scan + pool=10) | p99 **may** climb; laptop often stays ~60 ms — say so | `appsettings.Development.json` 9 |
| Beat 3 — replica | INSERT on replica; POST then replica `SELECT` by id; GET API | INSERT **error**; SELECT 0 rows or lag; GET **200** | `TadkaReadDbContext.cs`; `Program.cs` 16–22 |
| Partition | `day05-partition-demo.sql` — read `\echo` banners | A2 `Subplans Removed`; B1 vs B2 `Buffers:` | Throwaway tables, not `ordering.orders` |
| History | EXPLAIN OFFSET vs `GET /orders/history` | OFFSET walks thousands of rows; JSON has `nextCursor`, no `totalCount` | `OrdersController.cs` 168–210 |

Spoken cue in class: **"Ab demo."**

---

## 0. Fresh start (pre-class)

```powershell
git checkout day-05
docker compose down -v
docker rm -f tadka-postgres tadka-postgres-replica
docker volume rm tadka_pgdata tadka_pgdata_replica tadka-cohort_pgdata
docker compose up -d
docker compose ps
dotnet build Tadka.slnx
dotnet test Tadka.slnx
dotnet run --project src/Tadka.Api
```

**Look for:** both containers **(healthy)**. Tests **23 cases**. Listen **5224**. Replica log: `started streaming WAL from primary`.

## 0b. Leftover from Day 4 — SMS after commit (class start)

The **break** is drawn (SMS inside the txn un-confirms the order). Live is the **fix** already on this branch.

**1.** New **Created** order, copy `"id"`:

```powershell
curl.exe -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
```

**2.** Watch the **`dotnet run` terminal**, then PATCH. Paste the id — `$ORDER` is not set:

```powershell
curl.exe -s -w "`nHTTP %{http_code}`n" -X PATCH http://localhost:5224/api/v1/orders/PASTE_ID/status -H "Content-Type: application/json" --data-binary "@docs/runbooks/status-confirmed.json"
```

**Look for:** curl **204**. API log `Notification: order PASTE_ID confirmed — SMS sent to customer …`.

| If you see | Why |
|---|---|
| 404 `/orders//status` | Empty id. Paste the GUID. |
| 422, no log line | That order was already Confirmed. POST a new one. |
| 204 but no SMS line | You watched curl, not `dotnet run`. |

`SaveChanges` is `OrdersController.cs` **231**. `DispatchAsync` is **233** (after). Leave it that way.

## 1. Seed 200k orders

The break is invisible on 16 rows.

```powershell
docker cp scripts/day05-seed-large.sql tadka-postgres:/tmp/seed.sql
docker exec tadka-postgres psql -U tadka -d tadka -f /tmp/seed.sql
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT count(*) FROM ordering.orders;"
```

Expect roughly **200,001**. Run against the **primary**. It streams to the replica.

## 2. Beat 1 — induce the seq scan, then fix it

This is the order-history query: “Priya’s last 10 orders” — except the 200k seed uses customer `00000000-0000-0000-0000-000000000000`, **not** Priya. The API is `GET /api/v1/orders?customerId=…&pageSize=10`. Postgres sees:

```sql
SELECT * FROM ordering.orders
WHERE "CustomerId" = '00000000-0000-0000-0000-000000000000'
ORDER BY "CreatedAt" DESC
LIMIT 10;
```

Without an index it **reads the whole table**, then **sorts**, then keeps 10 rows. With `(CustomerId, CreatedAt DESC)` it walks the index and stops at 10. **No app restart.** You only drop/recreate indexes on the **primary**.

### 0. Seed first (or the break is invisible)

16 seed orders are one page. Seq scan and index scan both look instant. §1 must already show ~**200,001**:

```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT count(*) FROM ordering.orders;"
```

If that is ~16, run §1, then come back.

### 1. Prove the indexes exist (optional)

After `dotnet run`, EF created them. List:

```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT indexname FROM pg_indexes WHERE schemaname='ordering' AND indexname LIKE 'ix_orders%';"
```

**Look for two names:** `ix_orders_customer_id_created_at` and `ix_orders_created_at`. If they are missing, you are not on `day-05` or migrations did not run.

### 2. BREAK — drop the indexes and print the bad plan

**What the file does:** `DROP INDEX` those two names, then `EXPLAIN ANALYZE` the query above. You do **not** run EXPLAIN by hand.

```powershell
docker cp scripts/day05-induce-break.sql tadka-postgres:/tmp/break.sql
docker exec tadka-postgres psql -U tadka -d tadka -f /tmp/break.sql
```

**How you know it failed** (scroll the plan, read `Execution Time` out loud):

| In the output | Meaning |
|---|---|
| `Seq Scan` or `Parallel Seq Scan` on `orders` | Postgres walked the **table**, not an index |
| `Sort` | It ordered ~all matching rows in memory, then took 10 |
| `Execution Time: …` (often tens–hundreds of ms; capture laptop **~116 ms**) | Wall clock of this query. **Ratio** matters, not matching 116 exactly |

You just made production-shaped order history **slow on purpose**. The API still returns 200 — HTTP does not show the scan.

### 3. FIX — put the indexes back and print the good plan

**What the file does:** `CREATE INDEX` `(CustomerId, CreatedAt DESC)` and `(CreatedAt DESC)`, `ANALYZE`, **same** `EXPLAIN ANALYZE`.

```powershell
docker cp scripts/day05-apply-fix.sql tadka-postgres:/tmp/fix.sql
docker exec tadka-postgres psql -U tadka -d tadka -f /tmp/fix.sql
```

**How you know it is fixed:**

| In the output | Meaning |
|---|---|
| `Index Scan using ix_orders_customer_id_created_at` | Postgres used the composite index |
| **No** `Sort` node | Index is already `CreatedAt DESC` — leftmost prefix is **CustomerId** first |
| `Execution Time` much smaller (capture **~5 ms**, about **20×**) | Same query, cheap path |

Your milliseconds will differ. **116 → 5** is the captured ratio, not a pass/fail number.

**Code (same indexes, EF):** `src/Tadka.Api/Data/Configurations/OrderConfiguration.cs` 57–62. Deliberately **not** indexed: `status`, `restaurant_id` (no query filters on them → write tax for nothing).

### If you do not see Seq Scan

| What you saw | Why |
|---|---|
| Fast plan, no Seq Scan, still have Sort or Index Scan | You skipped §1 seed, or already ran apply-fix |
| Empty / tiny row counts in the plan | You explained Priya `c1b2c3d4-…`. Use `00000000-0000-0000-0000-000000000000` |
| `DROP INDEX` then prompt, no plan | Old copy of the SQL (EXPLAIN is now **in** the file). Pull latest `day-05` |
| `relation does not exist` | API never migrated. `dotnet run` on `day-05` first |

## 3. Beat 2 — the pool (one slow query queues everyone)

**What we are proving:** each HTTP request borrows a DB connection from a **shared pool**. A seq scan holds that connection longer. Other endpoints wait. Shipped pool is **Max 50** (`appsettings.Development.json` line 9).

Postgres `max_connections` (usually 100) and how many sessions are open:

```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c "SHOW max_connections;"
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT count(*) FROM pg_stat_activity WHERE datname='tadka';"
```

Load URL (200k seed customer, **not** Priya):

`http://localhost:5224/api/v1/orders?customerId=00000000-0000-0000-0000-000000000000&pageSize=10`

### 1. BASELINE — indexes on, pool 50 (API already running)

```powershell
powershell -File scripts/measure-load.ps1 -Url "http://localhost:5224/api/v1/orders?customerId=00000000-0000-0000-0000-000000000000&pageSize=10" -Concurrency 40 -Total 400
```

**Look for:** a line `n=400 concurrency=40 errors=0` and `p50=… p95=… p99=…`. Write p99 down (often single-digit to ~20 ms when indexed).

If `n=0` or a C# compile error, you are on an old harness — pull latest `day-05`.

### 2. BREAK — seq scan + tiny pool

**Ctrl+C** the API (must stop, or the env var is ignored).

Drop indexes (same file as Beat 1 — prints the bad EXPLAIN again):

```powershell
docker cp scripts/day05-induce-break.sql tadka-postgres:/tmp/break.sql
docker exec tadka-postgres psql -U tadka -d tadka -f /tmp/break.sql
```

Start API with **10** connections (this shell only):

```powershell
$env:ConnectionStrings__TadkaDb = "Host=localhost;Port=5432;Database=tadka;Username=tadka;Password=tadka_local;Minimum Pool Size=1;Maximum Pool Size=10;Timeout=5"
dotnet run --project src/Tadka.Api
```

**Other terminal**, same load command as step 1.

**How you know it “failed”:** p99 **higher** than baseline, or `errors` > 0 (timeouts). Capture laptop cold+contended ~**1188 ms**.

**Honesty — say this out loud:** a **warm** laptop often stays p99 **~50–62 ms** even at concurrency 80. That does **not** mean the demo is fake. One machine rarely empties 10 connections. Real exhaustion is `4 app instances × pool 100 > Postgres max_connections 100`. PgBouncer is **Day 11**. HTTP is still 200 on successes — the symptom is **latency**, not 500s.

### 3. FIX — indexes back, default pool

Ctrl+C the API.

```powershell
Remove-Item Env:ConnectionStrings__TadkaDb -ErrorAction SilentlyContinue
docker cp scripts/day05-apply-fix.sql tadka-postgres:/tmp/fix.sql
docker exec tadka-postgres psql -U tadka -d tadka -f /tmp/fix.sql
dotnet run --project src/Tadka.Api
```

Same `measure-load.ps1` again. **Fixed if** p99 is back near the baseline (capture ~**19 ms**).

## 4. Beat 3 — the replica is real (lag, not a backup)

### 1. Prove streaming + read-only

```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT client_addr, state, sync_state FROM pg_stat_replication;"
docker exec tadka-postgres-replica psql -U tadka -d tadka -c "SELECT pg_is_in_recovery();"
```

**Look for:** at least one row in `pg_stat_replication` (`streaming`). `pg_is_in_recovery()` = **`t`**.

Write on the replica (must fail):

```powershell
docker exec tadka-postgres-replica psql -U tadka -d tadka -c "INSERT INTO restaurant.restaurants (id, name) VALUES (gen_random_uuid(), 'should-fail');"
```

**Failed (good) if:** `cannot execute INSERT in a read-only transaction` (or similar). If this **succeeds**, you hit the **primary** by mistake.

### 2. BREAK — read-your-writes on the replica (lag)

Place an order on the **API** (writes primary). Copy `"id"` from JSON.

```powershell
curl.exe -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
```

**Immediately** (same second) on the **replica**:

```powershell
docker exec tadka-postgres-replica psql -U tadka -d tadka -c "SELECT id FROM ordering.orders WHERE id = 'PASTE_ID';"
docker exec tadka-postgres-replica psql -U tadka -d tadka -c "SELECT now() - pg_last_xact_replay_timestamp() AS lag;"
```

**How you know lag is real:** `SELECT id` returns **0 rows**, **or** `lag` is a few hundred ms (capture ~**236 ms**).

If the row is **already there**, you were slow. **Do not fake 0 rows.** Show `lag` and say the window closed.

### 3. FIX — the app sends that GET to the primary

```powershell
curl.exe -s -w "`nHTTP %{http_code}`n" http://localhost:5224/api/v1/orders/PASTE_ID
```

**Fixed if:** HTTP **200** and the JSON is the order you just placed — even while replica SELECT was empty. That path uses **primary** (`OrdersController` `GetById`, not `_read`).

List/menu/history GETs use `TadkaReadDbContext` → replica (`Program.cs` 16–22). Tests fall back to one Postgres.

### Replica is not a backup

Do **not** `DELETE FROM ordering.orders` in class. A delete on the primary **copies to the replica in milliseconds**. Two copies of the mistake. Backup = another **time** (`pg_dump`), replica = another **machine**.

```powershell
docker exec tadka-postgres pg_dump -U tadka -d tadka -f /tmp/tadka-backup.sql
docker exec tadka-postgres ls -lh /tmp/tadka-backup.sql
```

That only proves a dump file exists. The teaching point is spoken: replica ≠ PITR.

## 5. Partitioning — standalone experiment (not `ordering.orders`)

**What the file does:** builds throwaway tables `ordering.orders_by_month` and `ordering.orders_by_status`, copies data, runs four EXPLAIN blocks with `\echo` banners. **The running API is unchanged.** Needs the 200k seed (§1).

```powershell
docker cp scripts/day05-partition-demo.sql tadka-postgres:/tmp/partition-demo.sql
docker exec tadka-postgres psql -U tadka -d tadka -f /tmp/partition-demo.sql
```

Scroll; do not read the whole wall. Four banners:

| Banner | What it ran | How you know |
|---|---|---|
| `--- A1: no date filter` | count by customer, all months | Plan visits **many** partitions (no pruning) |
| `--- A2: date filter matching ONE month` | same + this month’s `created_at` | **`Subplans Removed:`** (capture **7** of 8). **Win:** immutable `created_at` |
| `--- B1: … SAME partition` | 5k `UPDATE` amount, status unchanged | Note **`Buffers:`** (capture **54/54**) |
| `--- B2: … CROSS partition` | 5k `UPDATE` status Created→Delivered | **`Buffers:`** higher (capture **88/89, +63%**). **Fail:** mutable `status` = DELETE+INSERT |

**Fixed / decision:** do **not** partition `ordering.orders` today (ADR-017). Sharding is board-only (`hash % N` ~80% move vs consistent hashing ~1/N).

If A2 has no `Subplans Removed`: seed missing or “this month” partition empty — still read B1 vs B2 buffers.

## 6. Deep pagination — OFFSET walks, cursor seeks

Priya is **not** in the 200k seed. List API is **`page` / `pageSize`**, not `offset` / `limit`. Seed customer `00000000-0000-0000-0000-000000000000` has ~4,000 orders.

### 1. BREAK — OFFSET (walk and throw)

HTTP (page 800 × 5 = skip 3,995 rows):

```powershell
curl.exe -s "http://localhost:5224/api/v1/orders?customerId=00000000-0000-0000-0000-000000000000&page=800&pageSize=5"
```

**That JSON does not prove cost.** Proof is EXPLAIN (indexes should be **on** — run apply-fix if you left Beat 2 broken):

```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c 'EXPLAIN ANALYZE SELECT * FROM ordering.orders WHERE "CustomerId" = ''00000000-0000-0000-0000-000000000000'' ORDER BY "CreatedAt" DESC LIMIT 5 OFFSET 3995;'
```

**Failed if:** `actual rows` in the thousands (capture Sort/heap **~4000**), `Execution Time` tens–hundreds of ms (capture **~150 ms**). Postgres **read then discarded** the skipped rows.

PowerShell single quotes keep `"CustomerId"` intact. `''uuid''` is one SQL string.

### 2. FIX — keyset HTTP shape + toy timing

```powershell
curl.exe -s "http://localhost:5224/api/v1/orders/history?customerId=00000000-0000-0000-0000-000000000000&pageSize=10"
```

**Fixed (contract):** JSON has **`nextCursor`**, **no** `totalCount` / `totalPages`. Code: `OrdersController.cs` 168–210.

**Fixed (cost)** — same Postgres, not Tadka HTTP:

```powershell
cd toydemo\day-03-api-primitives\cursor-pagination-toy
node real-db.js --mode=break
node real-db.js --mode=fix
```

**Look for:** break = deep OFFSET, many rows examined; fix = keyset, ~page-size rows. Folder says Day 3 in old docs — **taught today (Day 5)**.

Need Node.js. If `node` is missing, the EXPLAIN in step 1 is the live proof; the toy is optional.

## Done when

- [ ] Both Postgres containers healthy; tests 23
- [ ] New order → PATCH Confirmed → **204** + notification line on `dotnet run`
- [ ] Seq Scan ~116 ms → Index Scan ~5 ms (ratio, not the exact ms)
- [ ] You can explain 409 vs pool vs replica lag as **different** problems
- [ ] Replica write rejected; CAP window shown or lag number spoken
- [ ] Partition demo: pruning vs mutable-key buffers
- [ ] `GET /orders/history` for `0000…0000` returns a cursor page

## Troubleshooting

| Symptom | What to do |
|---------|------------|
| Replica never healthy | `down -v` then `up -d`. It clones the primary on first boot. |
| `42P07` / relation exists | Old volume. Wipe **both** `pgdata` volumes (§0). |
| History empty / tiny | You used Priya. Use `00000000-0000-0000-0000-000000000000` after the 200k seed. |
| `offset=` ignored | List API is `page` / `pageSize`. History is `cursor` / `pageSize`. |
| No SMS log | New Created order; watch **`dotnet run`**, not curl. |
| CAP replica already has the row | You were slow. Show `now() - pg_last_xact_replay_timestamp()`. Do not fake 0 rows. |
| `measure-load.ps1` / `n=0` | `powershell -File scripts/measure-load.ps1 …` from repo root. API must be up. Pull latest `day-05` if `Invoke-One` errors. |
| `node` not found | Optional toy. Beat 6 EXPLAIN OFFSET is enough. |

Next: Day 6 — Redis cache-aside + SSE. Not tonight.
