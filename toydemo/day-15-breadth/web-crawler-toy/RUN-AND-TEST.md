# RUN-AND-TEST.md for Web Crawler Toy

**Toy:** Web Crawler Toy
**Day Introduced:** Day 15 (breadth — crawler / search-index domain primer)
**Related Curriculum:** domain-primers web crawler, interview "design a web crawler", Zomato/Google search teardown contrast.
**Purpose:** Real HTTP site + crawler — show naive fast crawl getting **429 throttled**, burning budget on **spider traps**, and re-fetching **duplicate URLs** — vs politeness + visited set + trap depth cap.

## 1. Overview & Why This Toy Exists
"Crawl the web" sounds like BFS. Production needs **politeness** (per-host rate limit), **dedup** (visited set / bloom filter), and **trap detection** (infinite `?page=N` calendars). This toy runs a real local site that returns **429** when you hammer it, plus an infinite `/trap/N` chain.

**Teach with `real-crawl.js` only** — real HTTP server + real HTTP client.

## 2. The Failure Scenario
**Naive crawler:** max concurrency, no `visited`, follows every link including `/trap/0 → /trap/1 → …`.

**Bad symptoms:**
- Host returns **429** (blocked / throttled)
- Crawl budget wasted on trap pages, never reaching real catalog
- Same `/dup?n=1` and `/dup?n=2` bodies fetched repeatedly

## 3. Exact Steps — BREAK
**Prerequisites:** Node.js v18+. No Docker.

```powershell
cd tadka\toydemo\day-15-breadth\web-crawler-toy
node real-crawl.js --mode=break
```

**Observe (typical):**
- Pages fetched: **200** (hits `MAX_PAGES` cap still crawling trap)
- **429 throttled: dozens+** (hammered site faster than 8 req/s limit)
- **Trap pages entered: high** (most fetches are `/trap/N`)
- Queue still waiting: **large** (trap never ends)

## 4. The Fix
**Polite crawler:** `120ms` delay between requests, `visited` set skips duplicate URLs, trap links beyond depth 3 ignored.

```powershell
node real-crawl.js --mode=fix
```

**Observe (typical):**
- Pages fetched: **~20** (catalog + dup + shallow trap only)
- **429 throttled: 0**
- **Duplicate URLs skipped: 10+**
- **Trap pages entered: ≤ 4** (depth 0–3 only)

## 5. Verify the Fix
| Signal | break (naive) | fix (polite + dedup + trap guard) |
|--------|---------------|-----------------------------------|
| 429 responses | many | 0 |
| Trap depth reached | 100+ | ≤ 3 |
| Duplicate fetches | repeated | skipped via visited |
| Useful catalog pages | starved | crawled |

## 6. Full Run Instructions
```powershell
cd tadka\toydemo\day-15-breadth\web-crawler-toy
node real-crawl.js --mode=break
node real-crawl.js --mode=fix
```

**Optional trap-heavy break** (disable site throttle to isolate spider-trap waste):
```powershell
$env:SITE_RATE_LIMIT=999
node real-crawl.js --mode=break
```
Expect trap pages to dominate fetches (calendar `/trap/N` never ends).

**Tuning:**
- `$env:MAX_PAGES=500` — longer naive run, worse trap waste
- `$env:POLITE_DELAY_MS=200` — slower but safer fix run
- `$env:TRAP_MAX_DEPTH=5` — relax trap guard for discussion

## 7. Test Cases
| Test | Command | Expect |
|------|---------|--------|
| Throttle storm | `--mode=break` | 429 count > 0 |
| Trap waste | `--mode=break` | trap pages dominate fetches |
| Clean crawl | `--mode=fix` | 0 throttles, low trap count |
| Dedup | `--mode=fix` | duplicates skipped > 0 |

## 8. Troubleshooting
- **Port 31221 in use** — `$env:SITE_PORT=31222`
- **Fix still shows 429** — increase `POLITE_DELAY_MS` (site allows 8 req/s)
- **Break finishes too fast** — raise `MAX_PAGES`

## 9. Cross-Stack Notes
- **Production:** Redis frontier queue, per-host token bucket, bloom filter for URL dedup (billions of URLs).
- **Java:** StormCrawler, Norconex — same politeness + dedup primitives.
- **Go:** colly with `Visited` callbacks and `LimitRules`.

## 10. Curriculum Links
- Day 15 breadth + search-index toy (crawl → index pipeline)
- Zomato teardown (ES needs clean corpus)
- Pairs with `search-index-toy` — crawler feeds the inverted index

## 11. Failure-First Narrative
"In a real system you'd see your crawler IP-banned after hammering a partner site at 200 req/s. Worse, an infinite calendar trap eats your entire budget — 200 pages in and you never indexed a single restaurant menu. Duplicate query-string URLs re-fetch the same HTML. The fix is politeness per host, a visited set, and trap heuristics. After the fix you get zero 429s and the catalog is indexed in twenty fetches."

## 12. Limitations
- Single-host toy; no distributed frontier or robots.txt parser.
- Visited set is exact `Set` (not bloom filter) — honest for teaching, not petabyte scale.
- Trap guard is depth cap only; production uses URL pattern + redirect loop detection.