# RUN-AND-TEST.md for Cursor Pagination Toy

**Toy:** Cursor Pagination Toy
**Day Introduced:** Day 5 (deep pagination). Do not run on Day 3 or Day 4.
**Related Curriculum:** Day 5 Segment 6, ADR-046 `GET /api/v1/orders/history`.
**Purpose:** Failure-first demonstration of why OFFSET pagination dies at scale and how cursor/keyset pagination fixes it. Matches the "show it, don't just tell it" rule.

## 1. Overview & Why This Toy Exists
In Day 3 we discuss API contracts and pagination options (cursor vs offset). OFFSET is the naive default many devs reach for. This toy lets you *feel* the failure (high page numbers become slow/expensive) before we teach the fix in the curriculum. It is the exact pattern recommended in prior reviews for "Cover / Add (Missing HLD Nuances)".

## 2. The Failure Scenario
You have a large "order history" or "feed" table (100k+ rows). Users want to paginate deep (e.g. "show me page 4000 of my orders").

With OFFSET:
- To get page 4000 you do `LIMIT 20 OFFSET 80000`.
- The database must scan/skip 80,000 rows *every time*.
- Cost grows linearly with page depth.
- In real systems (especially with joins or on spinning disks / large tables without perfect covering indexes) p99 latency explodes, DB CPU is wasted, and "load more" at the end of a long list feels broken.

Expected bad symptoms: High latency on deep pages, high "rows examined", bad user experience for deep pagination.

## 3. Exact Steps to Induce & Observe the Break
**Prerequisites:** Node.js (any recent version). No Docker needed for the fast simulation.

**On Windows (PowerShell):**
```powershell
cd toydemo\day-03-api-primitives\cursor-pagination-toy
node index.js --mode=break
```

**On macOS/Linux:**
```bash
cd toydemo/day-03-api-primitives/cursor-pagination-toy
node index.js --mode=break
```

**Observe:**
- The script prints the equivalent real SQL for both modes.
- It shows simulated "rows examined" (the key teaching metric).
- High "rows examined" for deep OFFSET pages = exactly what you see in real EXPLAIN ANALYZE.

**Strongly recommended: see it on a real database (this is where it clicks)**
The pure JS version is intentionally lightweight and zero-dependency so you (or students) can run the "break vs fix" instantly during a live session.

**However, the pure JS simulation alone does not make "how it actually works" obvious.**

For real understanding you need to see:
- Query planner choices (Seq Scan / Bitmap Heap Scan + Filter vs Index Scan + Limit)
- "rows removed by filter"
- Buffer / IO cost ("shared hit", "shared read")
- Actual execution time difference on deep pages

See the "Real Database Verification (do this – it makes everything click)" section below. It uses the project's existing Postgres (the same container the main Tadka app uses) so there is no new infrastructure. The `real-db.js` helper makes this one command.

## 4. The Fix
Switch to cursor/keyset pagination:
- Client remembers the last seen ID from the previous page.
- Next request: `WHERE id > last_seen_id ORDER BY id LIMIT 20`.
- The database uses the index to jump straight to the right place. Cost is constant (only the page size), independent of how deep you are.

Apply the fix:
```bash
node index.js --mode=fix
```

## 5. Steps to Verify the Fix
Run the exact same high-page request in fix mode.
Compare output:
- Break: high "skipped rows work" + higher simulated time.
- Fix: constant low cost, fast even for "deep" pages.

The script prints clear before/after style numbers.

## 6. Full Run Instructions
**Quick smoke (break then fix) – zero dependencies:**
```bash
cd toydemo/day-03-api-primitives/cursor-pagination-toy
node index.js --mode=break
node index.js --mode=fix
```

**Real Database Verification (do this – it makes everything click)**
The project's Postgres is already running (from the main docker-compose).

1. Make sure the main Tadka Postgres is up:
   ```bash
   docker compose up -d postgres
   ```

2. Connect with psql (or use the pgAdmin that may be in your compose):
   ```bash
   docker exec -it tadka-postgres psql -U tadka -d tadka
   ```

3. Create a realistic test table + index (or use an existing large table like orders if it has enough rows):
   ```sql
   CREATE TABLE IF NOT EXISTS pagination_test (id bigserial PRIMARY KEY, payload text);
   INSERT INTO pagination_test (payload)
   SELECT 'row-' || g FROM generate_series(1, 100000) g;
   CREATE INDEX IF NOT EXISTS idx_pagination_test_id ON pagination_test(id);
   ANALYZE pagination_test;
   ```

4. Run the bad query and look at the plan + timing:
   ```sql
   EXPLAIN (ANALYZE, BUFFERS, TIMING ON)
   SELECT * FROM pagination_test
   ORDER BY id
   LIMIT 20 OFFSET 80000;
   ```

   You will see something like:
   - High "rows removed by filter" or many heap fetches.
   - The cost and actual time are much higher than they need to be.

5. Run the good (cursor) query:
   ```sql
   EXPLAIN (ANALYZE, BUFFERS, TIMING ON)
   SELECT * FROM pagination_test
   WHERE id > 80000
   ORDER BY id
   LIMIT 20;
   ```

   You will see:
   - Index Scan (or Index Only Scan) + Limit.
   - Tiny number of rows actually examined.
   - Much lower cost and execution time.

6. Try deeper pages (OFFSET 200000, cursor starting at 200000). The gap becomes dramatic.

This is the real behavior the JS toy is simulating. The JS version is just for speed during teaching; the EXPLAIN is what proves it in production.

**Clean up (optional):**
```sql
DROP TABLE pagination_test;
```

**Versions used for this toy:**
- Node: any recent (v18+ recommended)
- Postgres: the one from the project's docker-compose (Postgres 16)

## 7. Test Cases & Expected Results
| Test | Command | Broken Result | Fixed Result | Notes |
|------|---------|---------------|--------------|-------|
| Test 1: High page (sim page 4000) | `--mode=break` | High "scan cost" (skipped rows work ~80k), higher time | Low constant cost, fast | Simulates real DB row examination cost |
| Test 2: Cursor jump | `--mode=fix` | N/A | Constant cost regardless of "depth" | Uses lastId as cursor |

Run both modes and compare the printed numbers.

## 8. Troubleshooting
- "Command not found": Make sure you are in the toy directory and node is in PATH.
- Numbers look different: The simulation uses a simple loop for "work" — the *shape* (break = linear cost with depth, fix = constant) is what matters, not absolute ms.
- Want real DB feel: You can later extend this toy with a real SQLite/Postgres table + EXPLAIN ANALYZE (advanced exercise).

## 9. Cross-Stack Notes
- **Node.js (this toy):** Simple array filter/slice for demo. In real life use `sequelize.findAll({ where: { id: { [Op.gt]: lastId } }, limit, order })` or raw SQL with proper index.
- **Java/Spring:** `Pageable` with keyset or `WHERE id > ?` + `LIMIT`. Spring Data JPA + custom queries or QueryDSL.
- **Go:** `SELECT ... FROM orders WHERE id > $1 ORDER BY id LIMIT $2`.
- **.NET/EF Core:** Similar LINQ `Where(o => o.Id > lastId).Take(20).OrderBy(o => o.Id)`. Make sure there is an index on the sort column.
- The core idea (remember last seen key instead of offset count) is universal.

## 10. Curriculum Links
- Day 3 plan.md / option-space.md: Pagination trade-offs discussion.
- Day 3 api-style-selection.md and contracts.
- interview-pack (various solved problems mention pagination).
- Suggested note for day-03 plan: "Run `node toydemo/day-03-api-primitives/cursor-pagination-toy --mode=break` before class so students feel why OFFSET is dangerous at scale."

## 11. Failure-First Narrative (for slides/instructor)
"Imagine your order history has 100,000 orders. The user scrolls to 'page 4000'. With naive OFFSET the database has to examine and skip 80,000 rows *just to return the next 20*. Latency goes through the roof, the DB is doing useless work, and the experience is terrible. The fix is cursor pagination: the client says 'give me the next 20 after ID 80,123'. The database jumps straight there using the index. Cost stays constant no matter how deep the user has scrolled. This is the exact decision we teach in Day 3 when designing API contracts."

## 12. Limitations & What This Toy Does Not Cover
- The default JS version is a pure in-memory simulation (intentionally, for instant runs with zero dependencies, like the sharding-demo).
- The real pain (index usage, heap fetches, "rows removed by filter", buffer usage) only becomes obvious when you run the EXPLAIN ANALYZE commands against a real Postgres table.
- Does not cover every edge case (e.g. pagination on non-unique columns, using (created_at, id) as the cursor, handling deletes that create "holes", etc.).
- In real life you also have to think about cursor token encoding, authorization on the cursor, and what happens when the underlying data changes between pages.

**How to contribute updates to this toy:** 
- Improve the simulation or the real-DB instructions.
- Re-capture numbers / EXPLAIN output.
- Update this file + the living plan.
- Keep the failure-first flow.

---

*Recommended flow for teaching:*
1. Run the JS toy live (`--mode=break` then `--mode=fix`) so everyone sees the numbers instantly.
2. Then have the class (or you) paste the real EXPLAIN commands into the project's Postgres.
3. Discuss what the query planner actually does in each case.

This combination (fast simulation + real EXPLAIN) is the most effective way to make the concept stick.