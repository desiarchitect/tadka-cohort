# RUN-AND-TEST.md for Search / Inverted Index Toy

**Toy:** Search / Inverted Index Toy
**Day Introduced:** Day 15 (breadth teardown — search domain primer)
**Related Curriculum:** `domain-primers.md` search, Day 15 Zomato teardown (ES), interview "design a search engine".
**Purpose:** Show why `LIKE '%term%'` seq-scans Postgres at scale, and how a **GIN full-text index** (real inverted index) fixes it — with **EXPLAIN (ANALYZE, BUFFERS)** on the project's database.

## 1. Overview & Why This Toy Exists
Tadka menu browse is OLTP. Search is a different system design problem. Students say "add an index" but a B-tree on `name` does not fix `LIKE '%biryani%'`. You need an **inverted index** — in Postgres that's `to_tsvector` + **GIN**; at scale that's Elasticsearch.

**Teach with `real-db.js` first.** The JS simulation (`index.js`) is optional quick smoke only.

## 2. The Failure Scenario
**Query:** find docs matching `biryani` AND `koramangala` in 100k restaurant blurbs.

```sql
WHERE body ILIKE '%biryani%' AND body ILIKE '%koramangala%'
```

**Planner:** `Seq Scan` on all rows, high **"rows removed by Filter"**.

## 3. Exact Steps — BREAK (real Postgres)
**Prerequisites:** Docker, project's Postgres.

```powershell
cd D:\work\desi-architect\tadka
docker compose up -d postgres

cd toydemo\day-15-breadth\search-index-toy
node real-db.js --mode=break
```

**Observe in EXPLAIN output:**
- `Seq Scan on search_demo`
- `rows=100000` examined, thousands filtered
- Execution time tens of ms (grows linearly with corpus)

## 4. The Fix
```sql
CREATE INDEX idx_search_demo_fts
  ON search_demo USING GIN (to_tsvector('english', body));

WHERE to_tsvector('english', body) @@ to_tsquery('english', 'biryani & koramangala')
```

```powershell
node real-db.js --mode=fix
```

**Observe:**
- `Bitmap Index Scan` / `GIN` index
- Much lower execution time on same 100k rows
- Same match count as break mode

## 5. Verify the Fix
| Signal | break (LIKE) | fix (GIN FTS) |
|--------|--------------|---------------|
| Scan type | Seq Scan | Bitmap Index Scan on GIN |
| Rows touched | ~all rows | posting lists only |
| Match count | same | same |
| Execution time | higher | lower |

## 6. Full Run Instructions
**Recommended (real DB):**
```powershell
docker compose up -d postgres
node real-db.js --mode=break
node real-db.js --mode=fix
```

**Optional simulation (no Docker):**
```powershell
node index.js --mode=break
node index.js --mode=fix
```

**Scale:** `$env:ROW_TARGET=200000` before `real-db.js` (re-seeds if table smaller).

## 7. Test Cases
| Test | Command | Expect |
|------|---------|--------|
| Real break | `real-db.js --mode=break` | Seq Scan, ~100k rows examined |
| Real fix | `real-db.js --mode=fix` | GIN index scan, faster |
| Match parity | both | same `count(*)` printed before EXPLAIN |

## 8. Troubleshooting
- **`No such container: tadka-postgres`** — run `docker compose up -d postgres` from `tadka/`. Do not paste `#` comments on Windows.
- **Syntax error on EXPLAIN** — use the provided script (stdin pipe fix for Windows).
- **Fix slower first run** — GIN index build on 100k rows; second run is the teaching run.

## 9. Cross-Stack Notes
- **Postgres GIN** = inverted index for full-text (cohort-local, no new infra).
- **Elasticsearch** = distributed inverted index + BM25 ranking (Zomato teardown).
- **Java/Spring:** never `findByNameContaining` at scale without search engine.

## 10. Curriculum Links
- Day 15 Zomato ES contrast
- domain-primers search engine
- Tadka deliberately does not implement menu search in OLTP

## 11. Failure-First Narrative
"You typed LIKE into Postgres. The planner seq-scanned a hundred thousand menus. Every search paid full table rent. Full-text GIN is an inverted index sitting inside Postgres — token to row ids, intersect, done. Elasticsearch is that idea at Swiggy scale with ranking and shards. OLTP Postgres was never the search engine."

## 12. Limitations
- Postgres FTS is simpler than ES (no BM25 tuning, no cluster).
- English tokenizer only in demo.
- Does not replace Day 15 ES teardown — complements it with something students can run locally.

---

*Recommended flow:* `real-db.js` break → students read Seq Scan → `real-db.js` fix → Bitmap/GIN → slide: "ES is this at billion-doc scale."