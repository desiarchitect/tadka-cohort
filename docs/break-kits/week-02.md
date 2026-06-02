# Break Kit — Week 2: The Dinner-Rush Incident

> **Scenario.** Tadka has been live in Bangalore for a few weeks. Volume is still ~1 lakh orders/day — nothing alarming on a daily chart. But marketing ran an 8 PM promo, and tonight the dinner rush is brutal: customers complain the app "hangs," menus take 10 seconds to load, and a handful of orders fail outright. The daily total was *fine*. So what broke?
>
> **The lesson.** At this scale, **concurrency breaks you before volume does.** A few unindexed queries, under hundreds of simultaneous dinner-rush users, hold database connections open long enough to exhaust the pool — and once the pool is empty, *every* request queues, including ones that have nothing to do with the slow query. You will make this happen, watch it, diagnose it, and fix it cheapest-first.

This is the **reference Break Kit**. Every later week follows the same ritual; see [README.md](README.md) to author the others.

---

## Learning objectives

By the end of this session a student can:

1. Explain why **total daily volume is the wrong number** — and compute the peak concurrency that actually matters.
2. Read an `EXPLAIN ANALYZE` plan and recognize a `Seq Scan` as the root cause.
3. Connect a slow query to **connection-pool exhaustion** and the resulting platform-wide latency.
4. Walk the [cheapest-first scaling tree](../scaling-decision-tree.md) and justify *indexes + pool tuning + cache* over *replica / vertical scale*.
5. Prove the fix worked with a **before/after measurement**, and write the ADR.

---

## The ritual (every Break Kit follows this)

```
Baseline → Break it → Read the symptoms → Enumerate the options
   → Decide (ADR) → Fix → Re-test → Compare
```

The deliverable is the **before/after numbers and the ADR** — not the code.

---

## Prerequisites

- Tadka running locally (`docker compose up -d`, then `dotnet run --project src/Tadka.Api`).
- Seed data present (3 restaurants + menus; see [database-schema.md](../database-schema.md)).
- [k6](https://k6.io/docs/get-started/installation/) installed.
- `psql` or pgAdmin (http://localhost:5050) for SQL + `EXPLAIN ANALYZE`.

### Reproducing the "Week 1 baseline" on today's code

The current codebase already contains the Week-2 fixes (indexes and menu caching). To *demonstrate the break*, put the system back into its pre-fix state. Two options:

- **Cleanest:** check out the Week-1 release (`git checkout v0.0-scaffold` / the pre-index tag) in a throwaway worktree.
- **Fastest for a live demo (recommended):** stay on `main` and use the toggle knobs below — drop the indexes and shrink the pool without changing code.

---

## Step 0 — Set the stage (induce the break)

**1. Remove the indexes** (back to unindexed baseline):

```bash
psql "Host=localhost;Port=5432;Database=tadka;Username=tadka;Password=tadka_local" \
  -f scripts/week-02-induce-break.sql
```

> Optional but dramatic: uncomment the `generate_series` block in that script to load ~200k synthetic orders first, so the seq scans actually bite.

**2. Shrink the connection pool** so exhaustion happens fast and visibly. In `src/Tadka.Api/appsettings.Development.json`, append to the `TadkaDb` connection string:

```
;Maximum Pool Size=10;Timeout=5;CommandTimeout=15
```

Restart the API. (Default pool is 100 — large enough to hide the problem on a laptop. Ten makes the lesson land in 30 seconds.)

**3. (Optional) Bypass the menu cache** to show the read amplification: comment the cache-aside block in `RestaurantsController.GetMenu` so every menu request hits the DB. Leave it for now if you want to demonstrate caching as a *separate* fix in Step 5.

---

## Step 1 — Baseline → run the dinner rush

```bash
k6 run -e BASE_URL=http://localhost:5224 -e PEAK_VUS=150 k6/dinner-rush.js
```

**What you'll see (the break):**

- `http_req_duration{name:menu}` p99 climbs from ~50ms into **multiple seconds**.
- The `browse_errors` rate crosses 1% — those are requests that **timed out waiting for a free connection**, not slow queries.
- The k6 thresholds **fail** (red ✗). p99 < 300ms is blown.

This is the incident your customers felt. Note the numbers — this is your "before."

---

## Step 2 — Read the symptoms (diagnose)

**a) Find the slow query.** With the API under load, in psql:

```sql
EXPLAIN ANALYZE
SELECT * FROM restaurant.restaurants
WHERE lower(address_city) = lower('Bangalore')
ORDER BY name
LIMIT 10;
```

You'll see `Seq Scan on restaurants` and, on a bloated table, a `Sort` node — Postgres is reading every row and sorting in memory, per request.

**b) Watch the pool.** In the API logs you'll see Npgsql timeouts: *"The connection pool has been exhausted, either raise Maximum Pool Size (currently 10) or Timeout."*

**c) Make the causal chain explicit** (this is the architect's insight):

```
many concurrent users  ─┐
unindexed query (slow)  ─┼─► each request holds a connection longer
small pool (10)         ─┘   ─► pool drains ─► new requests QUEUE
                                    ─► p99 explodes for EVERYTHING,
                                       even endpoints that were never slow
```

> **Teaching beat:** the daily volume (1 lakh) never mattered. Concurrency × hold-time × pool-size did. Architects reason about the bottleneck resource, not the headline number.

---

## Step 3 — Enumerate the options (cheapest-first)

Walk the [scaling decision tree](../scaling-decision-tree.md) out loud:

| # | Option | Cost | Right call here? |
|---|--------|------|------------------|
| 1 | **Add indexes** | Very low | ✅ Yes — `EXPLAIN` proves seq scans. First move. |
| 2 | **Tune the connection pool** | Very low | ✅ Yes — pool of 10 is artificially tiny; size it to the workload. |
| 3 | **Cache the hot read (menu)** | Low | ✅ Yes — menus are read constantly, change rarely. Cache-aside. |
| 4 | Add a read replica | Medium | ❌ Not yet — the bottleneck is missing indexes, not read *volume*. (That's Week 3.) |
| 5 | Vertical scale the box | Medium | ❌ Masks the bug; you'd pay forever for a missing `CREATE INDEX`. |
| 6 | Shard / extract services | Extreme | ❌ Absurd at this scale. Naming it and rejecting it *is* the lesson. |

---

## Step 4 — Decide (write the ADR)

Record the decision while the evidence is fresh. ADRs to add/confirm:

- **Indexing strategy** — which indexes, justified by `EXPLAIN ANALYZE`, and explicitly *which you chose not to add* (see the `Status` debate in [indexing-strategy.md](../database/indexing-strategy.md)).
- **Connection pooling** — chosen `Maximum Pool Size`, and the reasoning (pool ≈ concurrent DB work, bounded by Postgres `max_connections`).
- **Menu caching** — cache-aside, TTL, and the invalidation rule (bust on availability toggle).

> Make **"Revisit when"** specific: e.g. *"revisit the pool size if p99 degrades while pool-wait time is near zero (then the DB, not the pool, is the limit)."* No boilerplate.

---

## Step 5 — Fix

**1. Indexes** — in the real project this is a code-first migration (`AddPerformanceIndexes`). For the demo toggle:

```bash
psql "$CONNSTR" -f scripts/week-02-apply-fix.sql
```

**2. Connection pool** — set a sane size in `appsettings.Development.json` (e.g. `Maximum Pool Size=50`) and remove the artificial `=10`.

**3. Menu cache-aside** — re-enable the cache block in `RestaurantsController.GetMenu` (it's already implemented), backed by Redis from `docker-compose.yml`. Confirm the availability-toggle endpoint busts the key.

---

## Step 6 — Re-test (same load, no other changes)

```bash
k6 run -e BASE_URL=http://localhost:5224 -e PEAK_VUS=150 k6/dinner-rush.js
```

Thresholds should now pass (green ✓): menu/list p99 back under 300ms, `browse_errors` ≈ 0.

---

## Step 7 — Compare (the deliverable)

Fill this in from your two runs:

| Metric | Before (broken) | After (fixed) |
|--------|-----------------|---------------|
| menu p99 | | |
| list p99 | | |
| browse error rate | | |
| restaurants query plan | `Seq Scan` | `Index Scan` |
| pool-timeout errors in logs | many | none |

A fix you can't show on this table is a guess. **Submit the table + the ADRs.**

---

## Discussion & stretch

- **Discussion:** Why didn't a bigger pool *alone* fix it? (It delays exhaustion but the slow query still scales badly — you'd just need an ever-bigger pool, and Postgres `max_connections` caps you.)
- **Discussion:** The menu cache introduces stale data. How stale is acceptable for a menu? Where is staleness *not* acceptable? (Foreshadows Week 3's CAP lesson.)
- **Stretch (senior):** Add `pg_stat_statements` and show the **DB query count** drop after enabling the cache — quantify the read amplification you removed.
- **Stretch (senior):** The composite `(CustomerId, CreatedAt DESC)` index lets Postgres skip the `Sort`. Prove it with `EXPLAIN ANALYZE` on the order-history query, before and after.

---

## Instructor notes

- On a fast laptop with little data, the break can be subtle. Use **both** levers — `generate_series` to bloat `orders`, and `Maximum Pool Size=10` — so the failure is unmistakable.
- Reset between groups: `scripts/week-02-induce-break.sql` (break) ⇄ `scripts/week-02-apply-fix.sql` (fix).
- If Redis isn't running, the current menu endpoint will error rather than fall back. For the no-cache baseline, prefer commenting the cache block over killing Redis, so you demonstrate *one* failure at a time.
- Keep the causal chain (Step 2c) on the whiteboard the whole session. It's the transferable idea; the SQL is just evidence.
