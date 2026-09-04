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
| Beat 2 — pool | Same load 3×: baseline → drop index + pool=10 → restore | p99 up = queue (tickets + long SQL). Laptop may stay ~60 ms | `appsettings.Development.json` 9 |
| Beat 3 — replica | INSERT on replica; POST; replica `SELECT` **same id**; GET API | read-only error; 0 rows or lag; GET **200** on primary | `GetById` primary; list/menu `_read` |
| Partition | `day05-partition-demo.sql` — read `\echo` banners | A2 `Subplans Removed`; B1 vs B2 `Buffers:` | Throwaway tables, not `ordering.orders` |
| History | EXPLAIN OFFSET vs `GET /orders/history` | OFFSET walks thousands of rows; JSON has `nextCursor`, no `totalCount` | `OrdersController.cs` 168–210 |

Spoken cue in class: **"Ab demo."**

---

## 0. Fresh start (pre-class)

**Why wipe:** Day 4 volume has no replica slot / old `ordering.orders`. `down` without `-v` leaves the disk. Next `dotnet run` can hit `42P07 relation already exists`, or the replica clones a stale primary.

```powershell
git checkout day-05
# Stop compose and DELETE volumes for THIS project.
docker compose down -v
# Shared name tadka-postgres: another clone may still own the container.
docker rm -f tadka-postgres tadka-postgres-replica
docker volume rm tadka_pgdata tadka_pgdata_replica tadka-cohort_pgdata
# Start PRIMARY 5432 and REPLICA 5433. Replica clones primary on first boot (slow).
docker compose up -d
docker compose ps
dotnet build Tadka.slnx
dotnet test Tadka.slnx          # 23 cases. Needs Docker (Testcontainers).
dotnet run --project src/Tadka.Api   # migrates PRIMARY; replica gets schema via WAL
```

**How you know you are ready:** both `(healthy)`. Replica logs `started streaming WAL from primary`. API listens **5224**. `volume rm` “no such volume” is fine.

## 0b. Leftover from Day 4 — SMS after commit (class start)

**The issue (drawn, not curled):** naive code sends SMS **inside** the same DB transaction as Confirm. Gateway 500 → ROLLBACK → order is **not** Confirmed. Restaurant clicked Confirm; SMS vendor is down; business fact died. Also `Order` now knows `_sms`.

**The fix already in this branch:** `Order` only **raises a fact** (`OrderConfirmedEvent`). Controller **saves first**, then runs the handler (log line = fake SMS). If the handler throws, the row stays Confirmed.

We cannot show the rollback live without flipping a leftover flag. Live = **prove dispatch is after commit**.

**Why a new order:** PATCH Confirmed only fires the event on **Created → Confirmed**. Beat 2 leftover order is already Confirmed → **422**, no log.

```powershell
# POST Priya + 2× biryani, no price. Server prices. Returns JSON including "id" and status Created.
# Quote @ so PowerShell does not splat the path. Copy the id GUID. $ORDER is never set.
curl.exe -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
```

Put the **`dotnet run` window** on screen (not this curl window). Then:

```powershell
# PATCH status Confirmed from a file (inline JSON breaks on PowerShell).
# Empty PASTE_ID → /orders//status → 404.
curl.exe -s -w "`nHTTP %{http_code}`n" -X PATCH http://localhost:5224/api/v1/orders/PASTE_ID/status -H "Content-Type: application/json" --data-binary "@docs/runbooks/status-confirmed.json"
```

**How we identified “it worked”:**

| Where | Look for |
|---|---|
| curl | **HTTP 204** (empty body) |
| `dotnet run` | `Notification: order PASTE_ID confirmed — SMS sent to customer …` |

That line is `OrderConfirmedNotificationHandler` (16–21), called from `DispatchAsync` at `OrdersController.cs` **233**, which is **below** `SaveChanges` **231**. Raise is `Order.cs` 81–84.

| If you see | Why |
|---|---|
| 404 `/orders//status` | Did not paste the id |
| 422, no log | Already Confirmed. POST a **new** order |
| 204, no SMS line | Watched curl, not `dotnet run` |

**We did not un-confirm.** GET still `"status": "Confirmed"` even if you imagine the log line throwing.

## 1. Seed 200k orders

**Why:** Beat 1 EXPLAIN on **16** demo orders is instant with or without an index (one page). Seq scan only **hurts** when the table is large. The file inserts **200,000** extra rows across 50 fake customers (`0000…0000` … `…0049`), `created_at` over ~180 days, then `ANALYZE`. Priya is **not** in that set.

```powershell
# Copy SQL into the PRIMARY container (not the replica — replica is read-only).
docker cp scripts/day05-seed-large.sql tadka-postgres:/tmp/seed.sql
# Run it. Takes a couple of minutes. WAL then streams copies to 5433.
docker exec tadka-postgres psql -U tadka -d tadka -f /tmp/seed.sql
# Proof it landed.
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT count(*) FROM ordering.orders;"
```

**How we know it worked:** count ~**200,001** (200k + leftover demo orders). If ~16, the file did not run or you queried the replica too early.

This seed is **required** for Beat 1, 2, 5, 6. Do it once per wiped volume.

## 2. Beat 1 — induce the seq scan, then fix it

**The issue:** order history SQL with no matching index **reads the whole table**, sorts, then keeps 10 rows. HTTP still 200 — you only see it in **EXPLAIN**.

This is “last 10 orders” — except the 200k seed uses customer `00000000-0000-0000-0000-000000000000`, **not** Priya. The API is `GET /api/v1/orders?customerId=…&pageSize=10`. Postgres sees:

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

## 3. Beat 2 — the pool (why we drop the index *again*, then run the *same* load)

Beat 1 was: **one query is slow** (seq scan). Beat 2 is: **that slow query poisons everything else**, because connections are **shared**.

### The story (say this before any command)

```
40 HTTP requests arrive at once  (measure-load -Concurrency 40)
        │
        ▼
   connection POOL     ← a waiting room of N tickets to Postgres
   shipped N = 50
   this break N = 10
        │
        ▼
   Postgres runs the SQL
```

Each request **borrows one ticket**, runs SQL, **returns the ticket**. The next waiter takes it.

- **Indexed (Beat 1 fix):** SQL ~5 ms. Ticket comes back fast. 10 tickets are plenty for 40 arrivals — they overlap in time, they do not all hold a ticket at once.
- **Seq scan (Beat 1 break):** SQL ~100 ms+. Ticket stays out longer. Now 40 arrivals and **10 tickets** means 30 people stand in the pool queue. Their HTTP time = wait-for-ticket + slow SQL. **Menu / health / someone else’s order history** use the **same 10 tickets**. One slow query makes the whole API feel down.

That is the link: **pooling is not “faster SQL.”** Pooling is a **cap on how many SQLs run at once**. A cap is fine when each SQL is short. A cap + a seq scan = a queue.

We change **two knobs** so the queue is visible:

| Knob | Command | Why |
|---|---|---|
| Make each SQL **long** | Drop the indexes again (`induce-break.sql`) | Same seq scan as Beat 1. Without this, pool=10 still looks fine. |
| Make tickets **few** | `Maximum Pool Size=10` | 40 concurrent HTTP vs 10 tickets. Shipped 50 hides the queue on a laptop. |

We **re-run the exact same** `measure-load.ps1` (same URL, 40 concurrent, 400 total) so the **only** things that changed are those two knobs. If you change the URL or skip the baseline, you cannot tell whether p99 moved because of the pool.

**Ctrl+C first:** `$env:ConnectionStrings__TadkaDb` is read when `dotnet run` **starts**. The already-running API still has pool 50.

### 0. How many tickets can Postgres itself take?

```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c "SHOW max_connections;"
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT count(*) FROM pg_stat_activity WHERE datname='tadka';"
```

`max_connections` is usually **100** (server cap). The app pool is a **second** cap in front of that. Shipped app pool: **50** (`appsettings.Development.json` line 9).

Load URL (200k seed customer, **not** Priya):

`http://localhost:5224/api/v1/orders?customerId=00000000-0000-0000-0000-000000000000&pageSize=10`

### 1. BASELINE — indexes ON, pool 50 (API already running from §0)

**What we did:** 400 GETs, 40 at a time, **fast** SQL, **50** tickets.

```powershell
powershell -File scripts/measure-load.ps1 -Url "http://localhost:5224/api/v1/orders?customerId=00000000-0000-0000-0000-000000000000&pageSize=10" -Concurrency 40 -Total 400
```

**How we identified “healthy”:** `n=400 concurrency=40 errors=0` and `p50=… p95=… p99=…`. **Write p99 on the board** (often ~5–20 ms). That number is the control.

If `n=0` or `Invoke-One` errors: pull latest `day-05` (harness was rewritten).

### 2. BREAK — seq scan + 10 tickets

**Why this file again?** After Beat 1 **fix**, the indexes are **back**. Pool demo needs them **gone** again. We are not re-teaching EXPLAIN. We are turning hold-time back up.

`day05-induce-break.sql` does two things in **one** run:

1. `DROP INDEX` `ix_orders_customer_id_created_at` and `ix_orders_created_at` (the two Beat 1 indexes).
2. `EXPLAIN ANALYZE` the history query so you **see** Seq Scan + Sort on screen.

It does **not** change C#, the pool, or the API. Only Postgres catalog: those indexes are missing until apply-fix.

**Line by line**

```powershell
# Copy the SQL file INTO the postgres container (path on your disk → /tmp/break.sql inside Docker).
docker cp scripts/day05-induce-break.sql tadka-postgres:/tmp/break.sql

# Run that file as user tadka on database tadka. Drops indexes, then prints EXPLAIN.
docker exec tadka-postgres psql -U tadka -d tadka -f /tmp/break.sql
```

**Look at that EXPLAIN before you continue.** You must see **Seq Scan** or **Parallel Seq Scan** and a **Sort**. If you still see **Index Scan**, the DROP did not run (wrong container, old SQL, or you are on primary vs a copy). Do not start the tiny pool yet — you would be measuring a fast query with 10 tickets (boring, p99 stays low).

**Why drop, in one sentence:** Npgsql only hands out **10** connections. Each connection is busy until SQL **finishes**. Seq scan keeps a connection busy ~20× longer than an index scan. 40 overlapping GETs then wait on those 10. **The pool did not get slower. Each borrower stayed longer.**

Then **Ctrl+C** the old API (still pool 50 in memory).

```powershell
# This process only. Does NOT edit appsettings.Development.json on disk.
# Maximum Pool Size=10 → at most 10 live Postgres sessions from this API.
# Timeout=5 → if no ticket for 5 seconds, the HTTP call fails (errors++).
$env:ConnectionStrings__TadkaDb = "Host=localhost;Port=5432;Database=tadka;Username=tadka;Password=tadka_local;Minimum Pool Size=1;Maximum Pool Size=10;Timeout=5"

# Start API. It reads the env var NOW. Leave this window running.
dotnet run --project src/Tadka.Api
```

**Other terminal** — identical load to baseline (do not change URL, 40, or 400):

```powershell
powershell -File scripts/measure-load.ps1 -Url "http://localhost:5224/api/v1/orders?customerId=00000000-0000-0000-0000-000000000000&pageSize=10" -Concurrency 40 -Total 400
```

**What that script does:** 400 HTTP GETs to order history; at most 40 in flight; prints `n`, `errors`, `p50/p95/p99` of **end-to-end HTTP time** (queue + SQL + network). It does not talk to Docker. It does not drop indexes.

**How we identified the issue** — compare **this p99** to **step 1 p99**:

| Output | What happened |
|---|---|
| p99 **up** (capture cold ~**1188 ms** vs baseline ~**19 ms**) | Many requests waited for a free connection, then ran slow SQL |
| `errors` > 0 | Waited 5s, no ticket (`Timeout=5`) |
| HTTP **200** on the rest | Not a crash. Dinner rush = **slow**, not 500 |

**Honesty:** warm laptop p99 often **~50–62 ms**. Queue may not fill on one box. Model still holds. Day 11 = many APIs vs `max_connections`.

### 3. FIX — why putting the index **back** drops p99

The pool is still a waiting room. We did **not** “fix the pool.” We made each visit **short** again.

`day05-apply-fix.sql`: `CREATE INDEX` those two names, `ANALYZE`, **same** EXPLAIN. You should see **Index Scan using ix_orders_customer_id_created_at**, **no Sort**, ~**5 ms**.

Now each GET holds a ticket ~5 ms instead of ~100 ms. 10 (or 50) tickets turn over fast. The 40 concurrent callers almost never wait. **p99 is HTTP time; HTTP time was queue + SQL; SQL collapsed; p99 collapses.**

That is why **the same** `measure-load.ps1` a third time is the proof: same 400 GETs, same 40-wide. If p99 is **near step 1** (capture ~**19 ms**), the queue is gone because **SQL is cheap again**, not because we invented a bigger pool (we actually restore Max **50**, which is extra headroom).

Ctrl+C the API first.

```powershell
# Forget pool=10. Next dotnet run uses appsettings Max 50.
Remove-Item Env:ConnectionStrings__TadkaDb -ErrorAction SilentlyContinue

# Copy + run FIX sql: CREATE INDEX + ANALYZE + EXPLAIN (must show Index Scan).
docker cp scripts/day05-apply-fix.sql tadka-postgres:/tmp/fix.sql
docker exec tadka-postgres psql -U tadka -d tadka -f /tmp/fix.sql

dotnet run --project src/Tadka.Api
```

Other terminal — **same** measure-load line as steps 1 and 2.

**Fixed if:** p99 ≈ baseline. EXPLAIN from apply-fix is Index Scan. If p99 is still high, indexes did not come back (EXPLAIN still Seq Scan) or you forgot to Remove-Item the env var (still pool 10 — should still be OK if SQL is 5 ms).

**Takeaway:** production fix is **index the query**. Shrinking the pool was a **microscope** so the queue showed up. Do not ship Max=10 as the “fix.” Growing Max to 100 only postpones the queue and can hit Postgres `max_connections`.

## 4. Beat 3 — the replica is real (lag, not a backup)

Beat 1–2 lived on **one** Postgres (primary, port **5432**, container `tadka-postgres`). Beat 3 is the **second** machine: replica port **5433**, container `tadka-postgres-replica`.

### The story (say this before any command)

```
  POST /orders, PATCH status     GET /restaurants, GET /orders?customerId=
           │                              │
           ▼                              ▼
     PRIMARY 5432                    REPLICA 5433
     (writes + "I just wrote this")  (copies PRIMARY via WAL, a few 100 ms late)
```

**Why a replica at all?** Dinner-rush **reads** (browse menu, list history) outnumber writes. Those reads steal CPU from checkout. Replica takes **stale-tolerant** GETs. That is ADR-016.

**The issue we are hunting:** replication is **not instant**. WAL has to ship and replay. If the app reads “the order I just placed” from the replica in that window, Postgres answers **0 rows**. The user paid; the app says 404. That is **not** a missing index. That is **read-your-writes** on a lagging copy.

**We do not “break” the replica.** We **observe** lag, then prove the **API** does not use the replica for that GET.

**The fix is routing, not “make lag zero.”** `GET /orders/{id}` uses the **primary** repository. List/menu/history use `_read` → replica (`Program.cs` 16–22). Tests use one Postgres (replica connection string falls back to primary).

```
Jo GET user ke APNE write ke turant baad aata hai  →  PRIMARY
Baaki reads (list, menu, history)                  →  REPLICA
```

### 1. Prove there are two machines, and the second only **plays back**

```powershell
# Ask the PRIMARY: is anyone streaming my WAL?
# Look for a row: state = streaming. Empty = replica not connected (down -v, up -d).
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT client_addr, state, sync_state FROM pg_stat_replication;"

# Ask the REPLICA: are you a standby?
# t = yes, I only replay. f = you are talking to a primary (wrong container).
docker exec tadka-postgres-replica psql -U tadka -d tadka -c "SELECT pg_is_in_recovery();"
```

**Why INSERT on the replica:** to prove it will not take writes. `tadka-postgres-replica` is in the command on purpose.

```powershell
docker exec tadka-postgres-replica psql -U tadka -d tadka -c "INSERT INTO restaurant.restaurants (id, name) VALUES (gen_random_uuid(), 'should-fail');"
```

**Identified (good) if:** `cannot execute INSERT in a read-only transaction`.  
**Wrong machine if:** the INSERT **succeeds** — that was the primary. Check container name `tadka-postgres-replica`.

### 2. BREAK — see lag (empty SELECT on replica)

**What we did:** write an order through the **API** (always primary). Then read that **same id** on the replica **in the same second**, bypassing the API.

```powershell
# POST body is Priya + Meghana biryani (no price). API inserts on PRIMARY, returns JSON.
# Copy the "id" field. Do not use $ORDER — paste the GUID.
curl.exe -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
```

**Immediately** — other line, replica, **that** id:

```powershell
# Direct SQL on the REPLICA. Not the API. Not the primary.
# 0 rows = WAL has not replayed this INSERT yet. That is the bug the user would see
# if GetById used the replica.
docker exec tadka-postgres-replica psql -U tadka -d tadka -c "SELECT id FROM ordering.orders WHERE id = 'PASTE_ID';"

# How far behind is replay? Capture ~236 ms. Not a pass/fail number.
docker exec tadka-postgres-replica psql -U tadka -d tadka -c "SELECT now() - pg_last_xact_replay_timestamp() AS lag;"
```

**How we identified the issue:**

| Output | Meaning |
|---|---|
| `SELECT id` → **0 rows** | Replica does not have the order yet. Lag window is open. |
| `lag` a few hundred ms | Same fact as a clock. |
| Row **already there** | You were slower than WAL. **Do not fake 0 rows.** Show `lag` and say the window closed. |

This is the **failure mode of ADR-016** if routing is wrong: place order → refresh → “order not found.”

### 3. FIX — same id, through the API (primary)

**Why this GET is the fix:** `OrdersController.GetById` uses `_orderRepository` → **primary** `TadkaDbContext`, not `_read`. So HTTP can 200 **while** replica SELECT is still empty.

```powershell
# API, not docker exec. Paste the SAME id as step 2.
curl.exe -s -w "`nHTTP %{http_code}`n" http://localhost:5224/api/v1/orders/PASTE_ID
```

**Fixed if:** HTTP **200** and the JSON is that order (Priya, Meghana, line items). That is read-your-writes.

**Contrast (optional):** `GET /api/v1/restaurants` uses `_read` (replica). Stale by milliseconds is OK for a menu. `GET /api/v1/orders?customerId=…` history is also replica.

**We did not make lag zero.** We **stopped using the replica** for the GET that must see the write.

### Replica is not a backup

Replica = **second machine**, same timeline, a bit late.  
Backup = **second time** (yesterday’s data).

A `DELETE` / `DROP` on the primary is sent on WAL too. The replica **copies the mistake**. Two empty databases is not recovery.

Do **not** run `DELETE FROM ordering.orders` in class.

```powershell
# Snapshot of PRIMARY right now, written inside the container. Proves "a backup file can exist."
# It does NOT prove replica ≠ backup. Say that out loud.
docker exec tadka-postgres pg_dump -U tadka -d tadka -f /tmp/tadka-backup.sql
docker exec tadka-postgres ls -lh /tmp/tadka-backup.sql
```

**RPO** = how much data you can lose (nightly dump → 24 h). **RTO** = how long to restore. Replica answers “primary box died.” Backup answers “we ran DELETE.”

**N replicas (class, no extra container):** two standbys at different lag → same user, two GETs, data can move **backwards**. Tadka stays on **one** replica. See `[S4-B7]` / ADR-016 Revisit-When.

## 5. Partitioning — experiment on **copies**, not the live `orders` table

**The issue:** one table, years of rows → vacuum, indexes, backups hurt. Instinct: split by month **or** by status.

**Why not touch `ordering.orders`:** ADR-017 is **defer**. The file builds **throwaway** tables, copies 200k rows, runs EXPLAIN, leaves the API alone. Needs §1 seed.

**Two experiments in one file**

- **A — partition by `created_at` (immutable).** A row stays in one month file forever. Filter “this month” → Postgres can **skip** other months (**pruning**).
- **B — partition by `status` (mutable).** Confirm/cancel **moves** the row: Postgres **DELETE + INSERT** across files, not an in-place UPDATE. That is the **trap**.

```powershell
# Copy onto PRIMARY (replica cannot CREATE TABLE).
docker cp scripts/day05-partition-demo.sql tadka-postgres:/tmp/partition-demo.sql
# Creates orders_by_month / orders_by_status, INSERTs, four EXPLAIN blocks with \echo banners.
docker exec tadka-postgres psql -U tadka -d tadka -f /tmp/partition-demo.sql
```

**Do not read the whole wall.** Search the four banners:

| Banner | What the SQL did | How we identified win / fail |
|---|---|---|
| `--- A1: no date filter` | `count(*)` one customer, **all** months | Plan touches **many** partitions. No prune possible. |
| `--- A2: date filter matching ONE month` | same + `created_at` this month | **`Subplans Removed:`** (capture **7** of 8). **Win.** |
| `--- B1: … SAME partition` | 5k `UPDATE` **amount**, status stays Created | Write down **`Buffers:`** (capture **54/54**) |
| `--- B2: … CROSS partition` | 5k `UPDATE` **status** Created→Delivered | **`Buffers:`** higher (capture **88/89, +63%**). **Fail / trap.** |

Clock on 5k updates is noisy — **Buffers / WAL**, not milliseconds.

**“Fix” today:** do **not** partition Tadka orders. Do **not** partition on a column that **changes**. Sharding (more **machines**) is board-only: `hash % N` moves ~80% of keys when you add a server; consistent hashing ~1/N.

If A2 has no `Subplans Removed`: seed missing or this month’s partition empty — still compare B1 vs B2 buffers.

## 6. Deep pagination — OFFSET walks, cursor seeks

**The issue:** loyal customer, ~4,000 orders, scrolls to the **end**. Naive API: `page=800`. Postgres `LIMIT 5 OFFSET 3995` does **not** skip. It **reads ~4000 rows, sorts, throws 3995 away**, returns 5. Cost grows with page number. Page 2 is cheap; page 800 is not.

Priya is **not** in the 200k seed. Use `00000000-0000-0000-0000-000000000000`. List API is **`page` / `pageSize`**, not `offset=` / `limit=` (those query params are **ignored**).

Indexes should be **on** (Beat 2 fix). If you left seq scan, OFFSET looks even worse and mixes two lessons.

### 1. BREAK — prove OFFSET cost (EXPLAIN, not the JSON)

```powershell
# Returns 5 orders as JSON. That does NOT show cost. You cannot see “threw 3995 away” in HTTP.
curl.exe -s "http://localhost:5224/api/v1/orders?customerId=00000000-0000-0000-0000-000000000000&page=800&pageSize=5"
```

Proof is SQL on the **primary** (PowerShell single quotes so `"CustomerId"` survives):

```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c 'EXPLAIN ANALYZE SELECT * FROM ordering.orders WHERE "CustomerId" = ''00000000-0000-0000-0000-000000000000'' ORDER BY "CreatedAt" DESC LIMIT 5 OFFSET 3995;'
```

**How we identified the issue:** `actual rows` in the **thousands** (capture ~**4000**), `Execution Time` tens–hundreds of ms (capture **~150 ms**). That is “walk and throw.”

### 2. FIX — keyset: “rows after this (time, id)”, not “skip N”

`GET /orders/history` uses `(CreatedAt, Id) < cursor` on the **same** composite index. Page 2 and page 800 cost about the **same**. Trade-off: **no** `totalCount`, **no** jump-to-page-40.

```powershell
curl.exe -s "http://localhost:5224/api/v1/orders/history?customerId=00000000-0000-0000-0000-000000000000&pageSize=10"
```

**How we know the contract is fixed:** JSON has **`nextCursor`**, **no** `totalCount` / `totalPages`. Empty `nextCursor` = last page. Code: `OrdersController.cs` 168–210 (ADR-046). Offset list stays for small admin pages.

**How we know the cost is fixed** (optional Node toy, same `tadka-postgres`):

```powershell
cd toydemo\day-03-api-primitives\cursor-pagination-toy
node real-db.js --mode=break    # deep OFFSET: many rows examined
node real-db.js --mode=fix      # keyset: ~page-size rows
```

Folder name still says day-03; **taught today**. No `node` → EXPLAIN in step 1 is enough.

**Why two endpoints:** one URL cannot be both “infinite scroll, any depth” and “jump to page 40 of 200.”

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
