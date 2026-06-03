# Day 5 — Runbook: Scale the Database (indexes, pool, read replica)

**Branch:** `day-05`  ·  **What's new:** performance **indexes** (see them flip `Seq Scan` → `Index Scan`), a tuned **connection pool**, and a real **streaming read replica** with an EF read/write split (read-heavy GETs → replica; writes + read-your-writes → primary). You also get a load harness to measure before/after.

> New here? Read [`README.md`](README.md). This day runs **two** Postgres containers (primary 5432 + replica 5433).

## 1. Run it (primary + replica)

```bash
git checkout day-05
docker compose up -d                 # starts postgres (5432) AND postgres-replica (5433)
docker compose ps                    # both healthy; replica logs: "started streaming WAL from primary"
dotnet run --project src/Tadka.Api   # migrates the PRIMARY; schema+seed stream to the replica via WAL
```

Verify the replica is live and **read-only**:
```bash
docker exec tadka-postgres-replica psql -U tadka -d tadka -c "SELECT count(*) FROM restaurant.restaurants;"   # 3 (streamed from primary)
docker exec tadka-postgres-replica psql -U tadka -d tadka -c "INSERT INTO restaurant.restaurants(id,name) VALUES (gen_random_uuid(),'x');"   # ERROR: read-only standby
curl -s http://localhost:5224/api/v1/restaurants    # served FROM the replica (read context)
```

## 2. Indexing — see the Seq Scan, then fix it (ADR-014)

EXPLAIN is meaningless on 16 rows — first bloat the `orders` table to ~200k:
```bash
# bash:   docker exec -i tadka-postgres psql -U tadka -d tadka < scripts/day05-seed-large.sql
# pwsh:   Get-Content scripts\day05-seed-large.sql -Raw | docker exec -i tadka-postgres psql -U tadka -d tadka
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT count(*) FROM ordering.orders;"   # ~200001
```

The order-history query (`WHERE customer_id = … ORDER BY created_at DESC`). **Before** the index — drop it and EXPLAIN:
```bash
docker exec -i tadka-postgres psql -U tadka -d tadka < scripts/day05-induce-break.sql     # drops the indexes
docker exec tadka-postgres psql -U tadka -d tadka -c "EXPLAIN ANALYZE SELECT * FROM ordering.orders WHERE \"CustomerId\"='00000000-0000-0000-0000-000000000000' ORDER BY \"CreatedAt\" DESC LIMIT 10;"
# → Parallel Seq Scan … Execution Time ~116 ms
```
**After** — recreate the composite index and EXPLAIN again:
```bash
docker exec -i tadka-postgres psql -U tadka -d tadka < scripts/day05-apply-fix.sql
docker exec tadka-postgres psql -U tadka -d tadka -c "EXPLAIN ANALYZE SELECT * FROM ordering.orders WHERE \"CustomerId\"='00000000-0000-0000-0000-000000000000' ORDER BY \"CreatedAt\" DESC LIMIT 10;"
# → Index Scan using ix_orders_customer_id_created_at … Execution Time ~5 ms   (≈20× faster, and the Sort is gone)
```
> Trade-off: every index taxes writes — we add only the two query-justified ones, and deliberately *don't* index `status`/`restaurant_id` (no query → zombie index).

## 3. Read replica + read-your-writes / CAP (ADR-016)

A read of an order you *just* placed must hit the **primary** (the replica is milliseconds behind). The app routes it correctly:
```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" \
  -d '{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"a1b2c3d4-0001-4000-8000-000000000001","items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":1}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.9,"longitude":77.7}}' | sed -E 's/.*"id":"([^"]+)".*/\1/')
curl -s -o /dev/null -w "GET just-placed order: %{http_code}\n" http://localhost:5224/api/v1/orders/$ORDER   # 200 (primary, always visible)
# see the actual replication lag window:
docker exec tadka-postgres-replica psql -U tadka -d tadka -c "SELECT round(extract(epoch from (now()-pg_last_xact_replay_timestamp()))*1000) AS lag_ms;"   # e.g. ~200 ms
```
> List/menu/order-history reads come from the replica (stale-tolerant); the just-placed-order read comes from the primary (read-your-writes).

## 4. Measure under load (before/after)

**No-install harness** (works anywhere):
```bash
# pwsh:
pwsh scripts/measure-load.ps1 -Url "http://localhost:5224/api/v1/orders?customerId=00000000-0000-0000-0000-000000000000&pageSize=10" -Concurrency 40 -Total 400 -Label "order-history"
# → prints errors% + p50/p95/p99.  Fixed (indexed, pool=50): p99 ~19 ms.
```
**Canonical k6 dinner-rush** (install once: `winget install GrafanaLabs.k6`):
```bash
k6 run -e BASE_URL=http://localhost:5224 -e ORDER_CUSTOMER_ID=00000000-0000-0000-0000-000000000001 k6/dinner-rush.js
# → 150 VUs/4 min, checks 100%, browse_errors 0%, list/menu p99 < 6 ms.  (Use a NON-EMPTY customer id — Guid.Empty fails validation.)
```

**Connection-pool exhaustion** (ADR-015) — reproduce the "everything goes slow" break by shrinking the pool while the index is dropped, then load it:
```bash
# pwsh: drop index, start the app with a tiny pool via an env override, then hammer it
Get-Content scripts\day05-induce-break.sql -Raw | docker exec -i tadka-postgres psql -U tadka -d tadka
$env:ConnectionStrings__TadkaDb="Host=localhost;Port=5432;Database=tadka;Username=tadka;Password=tadka_local;Maximum Pool Size=10;Timeout=5"
dotnet run --project src/Tadka.Api    # (in one terminal)
# in another: pwsh scripts/measure-load.ps1 -Url "...orders?customerId=00000000-0000-0000-0000-000000000000" -Concurrency 40 -Total 400 -Label "pool=10"
# → p99 collapses (hundreds–1000+ ms under contention). Restore: stop app, run day05-apply-fix.sql, restart with default pool.
```
> Honest note: on a single warm laptop you can't fully *exhaust* 10 connections — exhaustion is `concurrency × hold-time × pool-size`, a **multi-instance** failure (4 instances × default pool ≫ Postgres `max_connections`), which is why PgBouncer is earned at Day 11. The contention p99 spike is still the visible symptom.

## ✅ Done when

- [ ] Both Postgres containers healthy; replica returns seeded rows and **rejects writes**.
- [ ] `EXPLAIN` shows **Seq Scan (~116 ms)** before the index and **Index Scan (~5 ms)** after, on 200k rows.
- [ ] A just-placed order is visible via `GET /orders/{id}` (primary); the replica shows a real lag (~hundreds of ms).
- [ ] The harness (or k6) prints before/after numbers; you can name the trade-off of each fix.
- [ ] `dotnet test` still green (19/19; the read context falls back to the primary in tests).

## Troubleshooting

- **Replica won't start / not healthy:** it clones the primary on first boot. Reset cleanly: `docker compose down -v && docker compose up -d` (lets the primary re-init the replication role and the replica re-clone).
- **`pwsh` not found:** use Windows PowerShell (`powershell`) — the harness works in 5.1 too.

➡️ Next: [day-06.md](day-06.md) — Redis cache + live order tracking.
