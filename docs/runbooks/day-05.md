# Day 5 — Runbook: indexes, pool, replica, leftover events, history cursor

**Branch:** `day-05`. **What's new:** performance indexes (ADR-014), Npgsql pool Min 5 / Max 50 (ADR-015), streaming replica `:5433` + `TadkaReadDbContext` (ADR-016), partition **SQL experiment** (ADR-017), `GET /api/v1/orders/history` (ADR-046). Day 4's `OrderConfirmed` handler **stays**. **No Redis.**

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
| Beat 1 — seq scan | `day05-induce-break.sql` then EXPLAIN | **Parallel Seq Scan** + **Sort** ~116 ms | Dropped indexes |
| Beat 1 — fix | `day05-apply-fix.sql` then EXPLAIN | **Index Scan** ~5 ms, Sort gone | `OrderConfiguration.cs` 57–62 |
| Beat 2 — pool | `measure-load.ps1` with pool=10 + break | p99 climbs; honesty: laptop may stay ~60 ms warm | `appsettings.Development.json` 9 `Maximum Pool Size=50` |
| Beat 3 — replica | POST order, **immediately** replica `SELECT` | 0 rows or lag ~236 ms; API GET still **200** | `TadkaReadDbContext.cs`; `Program.cs` 16–22; `GET /orders/{id}` uses primary |
| Partition | `day05-partition-demo.sql` | `Subplans Removed: 7`; status key buffers **+63%** | Throwaway tables, not `ordering.orders` |
| History | `GET /api/v1/orders/history?customerId=0000…0000` | Cursor page, no `totalCount` | `OrdersController.cs` 168–210 |

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

**Break:**

```powershell
docker cp scripts/day05-induce-break.sql tadka-postgres:/tmp/break.sql
docker exec tadka-postgres psql -U tadka -d tadka -f /tmp/break.sql
```

Look for **`Parallel Seq Scan`** plus a **`Sort`**. Read the execution time out loud. Ratio is the lesson, not the milliseconds.

**Fix:**

```powershell
docker cp scripts/day05-apply-fix.sql tadka-postgres:/tmp/fix.sql
docker exec tadka-postgres psql -U tadka -d tadka -f /tmp/fix.sql
```

Same EXPLAIN: **Index Scan**, Sort gone. Index is `(customer_id, created_at DESC)` — leftmost prefix is `customer_id` first.

**Code:** `src/Tadka.Api/Data/Configurations/OrderConfiguration.cs` 57–62. Deliberately **not** indexed: `status`, `restaurant_id`.

## 3. Beat 2 — the pool

Each connection is an OS process (~1–3 MB):

```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c "SHOW max_connections;"
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT count(*) FROM pg_stat_activity WHERE datname='tadka';"
```

Shipped pool is **Max 50** (`appsettings.Development.json` line 9). To feel the squeeze in class: induce-break again, set `Maximum Pool Size=10` in that connection string, restart the API, then:

```powershell
pwsh scripts/measure-load.ps1 -Url "http://localhost:5224/api/v1/orders?customerId=00000000-0000-0000-0000-000000000000&pageSize=10" -Concurrency 40 -Total 400
```

**Honesty:** a warm laptop often holds p99 ~50–62 ms even at concurrency 80. The dramatic ~1188 ms is cold + seq scan + pool=10. Say so. Real exhaustion is `4 instances × 100 > max_connections 100` — PgBouncer is **Day 11**.

Then apply-fix and restore Max 50.

## 4. Beat 3 — the replica is real

```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT client_addr, state, sync_state FROM pg_stat_replication;"
docker exec tadka-postgres-replica psql -U tadka -d tadka -c "SELECT pg_is_in_recovery();"
```

`pg_is_in_recovery()` must be `t`. Replica **rejects writes** (`cannot execute INSERT in a read-only transaction`).

**CAP you can feel** — POST, then query the replica **immediately**:

```powershell
curl.exe -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
docker exec tadka-postgres-replica psql -U tadka -d tadka -c "SELECT now() - pg_last_xact_replay_timestamp() AS lag;"
```

Paste the new id into a replica `SELECT` **fast**. Zero rows (or lag ~236 ms) is the lesson. `GET /api/v1/orders/PASTE_ID` still **200** — that path uses the **primary**. If the row has already replicated, **do not fake it** — show the lag number.

**Replica is not a backup.** A `DELETE` on the primary reaches the replica in milliseconds.

```powershell
docker exec tadka-postgres pg_dump -U tadka -d tadka -f /tmp/tadka-backup.sql
```

**Code:** `TadkaReadDbContext.cs`; `Program.cs` 16–22 (fallback to primary if replica unset — tests do this).

## 5. Partitioning — standalone experiment

Nothing in the app schema changes.

```powershell
docker cp scripts/day05-partition-demo.sql tadka-postgres:/tmp/partition-demo.sql
docker exec tadka-postgres psql -U tadka -d tadka -f /tmp/partition-demo.sql
```

| Result | Meaning |
|---|---|
| `Subplans Removed: 7` | Range on immutable `created_at` — pruning works |
| buffers **54 → 88 (+63%)** | List on mutable `status` — every update is DELETE+INSERT across partitions |

**Do not partition `ordering.orders` today** (ADR-017). Sharding is board-only: `hash % N` moves ~80% on add-server; consistent hashing ~1/N.

## 6. Deep pagination

**Wrong:** `offset=` / `limit=` — the list API uses **`page` / `pageSize`**. Priya is **not** in the 200k seed.

Offset (walk-and-throw), seed customer:

```powershell
curl.exe -s "http://localhost:5224/api/v1/orders?customerId=00000000-0000-0000-0000-000000000000&page=800&pageSize=5"
```

Keyset (same cost at any depth):

```powershell
curl.exe -s "http://localhost:5224/api/v1/orders/history?customerId=00000000-0000-0000-0000-000000000000&pageSize=10"
```

**Look for:** `nextCursor`, **no** `totalCount`. Code: `OrdersController.cs` 168–210.

Optional toy (same Postgres):

```powershell
cd toydemo\day-03-api-primitives\cursor-pagination-toy
node real-db.js --mode=break
node real-db.js --mode=fix
```

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
| `measure-load.ps1` / `node` not found | `pwsh scripts/measure-load.ps1 …`. Install Node for the cursor toy. |

Next: Day 6 — Redis cache-aside + SSE. Not tonight.
