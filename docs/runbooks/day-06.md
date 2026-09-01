# Day 6 — Runbook: Redis cache-aside + SSE live tracking

**Branch:** `day-06`. **What's new (taught):** Redis cache-aside + delete-on-write (ADR-018), single-flight `SET NX EX` lock (ADR-019), SSE `GET /orders/{id}/events` over Redis pub/sub (ADR-020). **Leftover on the branch, not Sunday lecture:** nginx scale-out (047), ETag (048), rate limit (049), edge+signed URLs (050), SSE replay (051).

**Three containers:** Postgres `5432`, replica `5433`, **Redis `6379`**. HTTP **5224**.

> **Windows PowerShell:** `curl.exe`. Quote `@file`. `$RID` below is a PowerShell variable you set once.

| Thing | Value |
|-------|--------|
| Meghana | `a1b2c3d4-0001-4000-8000-000000000001` |
| Biryani | `b1b2c3d4-0001-4000-8000-000000000001` |
| Cache key | `restaurant:{Meghana}:menu` |
| TTL | ~60 s |

### Demo → code

| When | What | Look for | Code |
|---|---|---|---|
| Miss → hit | DEL key, GET menu twice | EXISTS 0→1, TTL ~60 | `RestaurantsController.cs` 113 `GetOrSetAsync`; `RedisCacheService.cs` 19–28 hit |
| Scan proof | 20 GETs, replica `pg_stat_user_tables` | **delta 0** | miss filled Redis; hits never touch DB |
| Delete-on-write | PATCH availability | EXISTS 0, next GET 1 | `RestaurantsController.cs` 157 `RemoveAsync` |
| Redis down | `docker compose stop redis` | menu **200**; SSE **503** | cache catch 62–66; `OrderTrackingController.cs` 31–35 |
| Stampede lock | (inspect, no herd) | `lock:restaurant:…` | `RedisCacheService.cs` 31–33 `StringSetAsync` NX |
| SSE | `curl.exe -N` + PATCH Confirmed | `event: Confirmed` | `OrderTrackingController.cs` 28; `RedisOrderTrackingBus.cs` 23, 45 |

---

## 0. Fresh start

```powershell
git checkout day-06
docker compose down -v
docker rm -f tadka-postgres tadka-postgres-replica tadka-redis
docker compose up -d
docker compose ps
docker exec tadka-redis redis-cli ping
dotnet test Tadka.slnx
dotnet run --project src/Tadka.Api
```

**Look for:** three healthy containers, **PONG**, tests **27/27**, listen **5224**.

Set once:

```powershell
$RID  = "a1b2c3d4-0001-4000-8000-000000000001"
$ITEM = "b1b2c3d4-0001-4000-8000-000000000001"
$KEY  = "restaurant:${RID}:menu"
```

## 1. Cache-aside — miss then hit (ADR-018)

```powershell
docker exec tadka-redis redis-cli DEL $KEY
docker exec tadka-redis redis-cli EXISTS $KEY
curl.exe -s -o NUL -w "HTTP %{http_code}`n" http://localhost:5224/api/v1/restaurants/$RID/menu
docker exec tadka-redis redis-cli EXISTS $KEY
docker exec tadka-redis redis-cli TTL $KEY
curl.exe -s -o NUL -w "HTTP %{http_code}`n" http://localhost:5224/api/v1/restaurants/$RID/menu
```

**Look for:** first GET miss (slower, ~282 ms class capture); EXISTS **1**; TTL **~60**; second GET faster (~23 ms).

Scan-count proof (replica, 20 hits, **delta 0**):

```powershell
$SQL = "select seq_scan+idx_scan from pg_stat_user_tables where schemaname='restaurant' and relname='menu_items';"
$B = docker exec tadka-postgres-replica psql -U tadka -d tadka -tAc $SQL
1..20 | ForEach-Object { curl.exe -s -o NUL http://localhost:5224/api/v1/restaurants/$RID/menu }
$A = docker exec tadka-postgres-replica psql -U tadka -d tadka -tAc $SQL
Write-Host "before=$B after=$A"
```

## 2. Delete-on-write

```powershell
curl.exe -s -o NUL -X PATCH http://localhost:5224/api/v1/restaurants/$RID/menu/$ITEM/availability -H "Content-Type: application/json" --data-binary "@docs/runbooks/menu-unavailable.json"
docker exec tadka-redis redis-cli EXISTS $KEY
curl.exe -s -o NUL http://localhost:5224/api/v1/restaurants/$RID/menu
docker exec tadka-redis redis-cli EXISTS $KEY
curl.exe -s -o NUL -X PATCH http://localhost:5224/api/v1/restaurants/$RID/menu/$ITEM/availability -H "Content-Type: application/json" --data-binary "@docs/runbooks/menu-available.json"
```

**Look for:** EXISTS **0** after PATCH, **1** after next GET. Never cache **order status** or **payment**.

## 3. Redis down — same tool, two classifications

```powershell
docker compose stop redis
curl.exe -s -o NUL -w "menu %{http_code}`n" http://localhost:5224/api/v1/restaurants/$RID/menu
curl.exe -s -o NUL -w "sse %{http_code}`n" http://localhost:5224/api/v1/orders/00000000-0000-0000-0000-000000000001/events
docker compose start redis
```

**Look for:** menu **200** (performance dep). SSE **503** (correctness dep for the stream). Orders still place without Redis.

## 4. Stampede lock (inspect, no 10k herd)

Code: `RedisCacheService.cs` 31–33 `lock:{key}` `SET NX EX` 5s. Waiters retry ~80 ms × 5 then hit DB.

You will not see an unlocked herd live (no disable lever). After a miss you may catch `lock:restaurant:…` with `redis-cli KEYS lock:*` if you are fast. **Hot key** (IPL one restaurant): recognize; do not solve today.

## 5. SSE live tracking (ADR-020)

**Terminal A** — new order, then stream. **`-N` is required** (otherwise nothing appears):

```powershell
curl.exe -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
curl.exe -N http://localhost:5224/api/v1/orders/PASTE_ID/events
```

**Terminal B:**

```powershell
curl.exe -s -w "`nHTTP %{http_code}`n" -X PATCH http://localhost:5224/api/v1/orders/PASTE_ID/status -H "Content-Type: application/json" --data-binary "@docs/runbooks/status-confirmed.json"
```

**Look for** in A: `event: Confirmed`. Channel `order:{id}` (`RedisOrderTrackingBus.cs` 23, 45). Replay/`Last-Event-ID` is on the branch — **not taught**.

## 6. Scale-out preview only (not the lecture)

```powershell
docker compose --profile scale-out up -d
curl.exe -i http://localhost:8090/health
docker stop tadka-api-2-1
docker start tadka-api-2-1
docker compose --profile scale-out down
```

Three questions, then **stop:** where is the cache; who counts rate limits; which box holds the SSE connection. Beats 4–10 = break-kit homework.

## Done when (Sunday)

- [ ] PONG; three containers healthy; 27/27
- [ ] Miss then hit; 20 GETs **+0** replica scans
- [ ] PATCH availability deletes the key
- [ ] Redis stopped: menu **200**, SSE **503**
- [ ] Two-terminal SSE prints Confirmed

## Troubleshooting

| Symptom | What to do |
|---------|------------|
| `tadka-redis` in use | `docker rm -f tadka-redis`; `docker compose up -d` |
| SSE blank | `curl.exe -N`. Redis up. 503 = Redis down (by design). |
| PATCH 400 | Use `@docs/runbooks/menu-unavailable.json`, not inline JSON. |
| Scan count moved | You DEL'd the key or Redis was down — hits were misses. |

HTTP caching / ETag / gzip: weekday, `day6-caching-compression` source. Next: Week 4 payment brownout.
