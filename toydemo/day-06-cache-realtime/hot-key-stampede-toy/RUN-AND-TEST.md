# RUN-AND-TEST.md for Hot Key / Cache Stampede Toy

**Toy:** Hot Key / Cache Stampede Toy
**Day Introduced:** Day 06 (Redis cache-aside, ADR-018/019)
**Related Curriculum:** ADR-019 cache-stampede-single-flight-lock, Day 6 break-kit, `RedisCacheService.cs`, cohort-prep day-06 stampede lab.
**Purpose:** Failure-first demo of thundering herd on hot-key cache expiry (200 DB refreshes) vs single-flight lock (~1 refresh).

## 1. Overview & Why This Toy Exists
Tadka Day 6 adds Redis cache-aside for the menu. Students feel the cache hit — but the **expiry boundary** is invisible until it hurts. When a viral restaurant's menu TTL expires at dinner rush, every concurrent request misses at once and hammers Postgres.

This toy makes ADR-019 tangible: count DB queries on one expiry event.

## 2. The Failure Scenario
**Workload:** Hot key `menu:restaurant:42` expires. 200 concurrent requests arrive (dinner rush).

**Naive cache-aside:** `GET cache → miss → query DB → SET cache` with no coordination.

**Bad symptoms:**
- 200 parallel DB queries for the same key
- Aggregate DB work: 200 × 50ms = 10,000ms of refresh work
- Connection pool exhaustion; p99 worse than no cache
- Repeats every TTL boundary

## 3. Exact Steps to Induce & Observe the Break
**Prerequisites:** Node.js v18+.

```powershell
cd tadka\toydemo\day-06-cache-realtime\hot-key-stampede-toy
node index.js --mode=break
```

**Observe:**
- `DB queries issued: 200`
- `Aggregate DB work: 10000ms`

**Real Redis (recommended):**
```powershell
cd tadka
docker compose up -d redis
cd toydemo\day-06-cache-realtime\hot-key-stampede-toy
npm install
node real-redis.js --mode=break
```

## 4. The Fix
**Single-flight lock (ADR-019):** On miss, `SET lock:{key} {token} NX EX`. Winner refreshes; losers wait and re-read cache.

```powershell
node index.js --mode=fix
node real-redis.js --mode=fix
```

## 5. Steps to Verify the Fix
| Metric | break | fix |
|--------|-------|-----|
| DB queries (sim) | 200 | 1 |
| Aggregate DB work | 10,000ms | 50ms |
| DB queries (real-redis) | ~150–200 | ~1–5 |

Request latency stays similar (~50–60ms) — the win is **DB protection**, not faster cache hits.

## 6. Full Run Instructions
**Quick smoke:**
```powershell
node index.js --mode=break
node index.js --mode=fix
```

**Real Redis:**
```powershell
docker compose up -d redis   # from tadka/
npm install
node real-redis.js --mode=break
node real-redis.js --mode=fix
```

**Larger stampede:**
```powershell
$env:CONCURRENT=500
node real-redis.js --mode=break
```

## 7. Test Cases & Expected Results
| Test | Command | Broken | Fixed |
|------|---------|--------|-------|
| Simulation stampede | `index.js --mode=break` | 200 DB queries | — |
| Simulation single-flight | `index.js --mode=fix` | — | 1 DB query |
| Real Redis stampede | `real-redis.js --mode=break` | High `db-queries` counter | — |
| Real Redis fix | `real-redis.js --mode=fix` | — | `db-queries` ≈ 1 |

## 8. Troubleshooting
- **Redis connection refused** — `docker compose up -d redis` from `tadka/`.
- **break shows fewer than 200 DB queries on real-redis** — race may let first writer populate cache before all miss; still far higher than fix. Increase `CONCURRENT`.
- **fix shows >5 DB queries** — fall-through after lock wait retries; acceptable; still orders of magnitude better than stampede.

## 9. Cross-Stack Notes
- **Tadka:** `RedisCacheService.GetOrSetAsync` — SET NX EX + token-guarded release.
- **Java:** Caffeine `LoadingCache` single-flight, or Redisson lock.
- **Go:** `singleflight.Group` from `golang.org/x/sync/singleflight`.
- **Node:** in-process mutex works on one pod only — Redis lock needed distributed (this toy's real-redis path).

## 10. Curriculum Links
- ADR-018 (cache-aside) + ADR-019 (stampede lock)
- Day 6 break-kit stampede lab
- Day 14 Scenario 4 (Redis-down fall-through — performance dep, not correctness)
- Maps directly to `GET /api/v1/restaurants/{id}/menu` hot path

## 11. Failure-First Narrative
"The menu cache worked beautifully all evening — 2ms responses. Then the TTL expired. Five hundred requests were in flight. Every single one missed. Every single one queried Postgres for the same menu. For three hundred milliseconds your database was hit harder than if you'd never added Redis. The fix isn't a bigger cache — it's single-flight: one winner refreshes, everyone else waits fifty milliseconds and reads the warm key. One DB query, not five hundred."

## 12. Limitations
- Single hot key only (not multi-key stampede).
- Simulated DB is `sleep(50)` + counter, not real Postgres.
- Does not demo probabilistic early expiration (ADR-019 alternative).
- In-process fix in `index.js` is not distributed — use `real-redis.js` for the Redis lock story.

---

*Teaching flow:* `index.js` for instant DB-query count contrast → `real-redis.js` to show the actual SET NX EX pattern students will recognize in `RedisCacheService.cs`.