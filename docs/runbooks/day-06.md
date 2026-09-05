# Day 6 — Runbook: Redis cache-aside + SSE live tracking

**Branch:** `day-06`. **What's new (taught):** Redis cache-aside + delete-on-write (ADR-018), single-flight `SET NX EX` lock (ADR-019), SSE `GET /orders/{id}/events` over Redis pub/sub (ADR-020). **Leftover on the branch, not Sunday lecture:** nginx scale-out (047), ETag (048), rate limit (049), edge+signed URLs (050), SSE replay (051).

**Three containers:** Postgres primary `5432`, replica `5433`, **Redis `6379`**. HTTP **5224**. Tests **27/27**.

> **Windows PowerShell:** use **`curl.exe`**. Quote `@file`. `$RID` is a PowerShell variable you set once — leave it **unquoted** so it expands. Redis CLI walkthrough (PING, SET NX, pub/sub): [`docs/database/redis-cli.md`](../database/redis-cli.md).

| Thing | Value |
|-------|--------|
| API | `http://localhost:5224` |
| Redis | `localhost:6379` · container `tadka-redis` |
| Meghana | `a1b2c3d4-0001-4000-8000-000000000001` |
| Biryani | `b1b2c3d4-0001-4000-8000-000000000001` |
| Cache key | `restaurant:{Meghana}:menu` |
| TTL | 60 s (`RestaurantsController` `MenuTtl`) |

Spoken cue in class: **"Ab demo."**

### Demo → code

| When | What you run | Look for | Code |
|---|---|---|---|
| Miss → hit | `DEL` key, GET menu twice with `time_total` | EXISTS 0→1, TTL ~60, second GET faster | `RestaurantsController.cs` 113 `GetOrSetAsync`; `RedisCacheService.cs` 26–28 hit |
| Scan proof | 20 GETs, replica `pg_stat_user_tables` | **delta 0** | hits never touch DB |
| Delete-on-write | PATCH availability | EXISTS 0, next GET → 1 | `RestaurantsController.cs` 157 `RemoveAsync` |
| Redis down | `docker compose stop redis` | menu **200**; SSE **503** | cache catch 62–66; `OrderTrackingController.cs` 31–35 |
| Stampede lock | inspect (no herd) | `SET NX EX` in code; `KEYS lock:*` often **empty** | `RedisCacheService.cs` 31–33 |
| SSE | `curl.exe -N` + PATCH Confirmed | `event: Confirmed` | `OrderTrackingController.cs` 28; `RedisOrderTrackingBus.cs` 23, 45 |

---

## How Redis is wired in Tadka

Redis is **not** “a cache library.” It is a **server**. The API is a **client**. Compose starts the server; .NET talks to it.

```
GET /restaurants/{id}/menu
        │
        ▼
  ICacheService.GetOrSetAsync          GET  restaurant:{id}:menu
        │                              SET  … EX 60          ┐
        ▼                              SET  lock:… NX EX 5   ├─ Redis :6379
  miss → TadkaReadDbContext (replica)  PUBLISH order:{id}    ┘
```

**Infra:** `docker-compose.yml` service `redis`, image `redis:7-alpine`, container `tadka-redis`, port **6379**. No volume — cache is disposable. Healthcheck is `redis-cli ping`.

**App config:** `appsettings.Development.json` → `ConnectionStrings:Redis` = `localhost:6379,abortConnect=false`. Scale-out API containers use `redis:6379` (compose DNS).

### .NET (what Tadka actually uses)

| Piece | Type | File |
|---|---|---|
| Client | **StackExchange.Redis** | `Tadka.Api.csproj` |
| Connection | one **`IConnectionMultiplexer`** singleton for the process | `Program.cs` 70–71 |
| Cache facade | `ICacheService` (`GetOrSetAsync`, `RemoveAsync`) | `Infrastructure/Caching/ICacheService.cs` |
| Cache impl | `RedisCacheService` — `StringGet` / `StringSet` + TTL, `SET NX EX` lock, `KeyDelete` | `RedisCacheService.cs` |
| If Redis is down on a **GET** | catch `RedisException` → run the DB factory → still HTTP **200** | `RedisCacheService.cs` 62–66 |
| If no connection string | `NullCacheService` (always miss) + `NullOrderTrackingBus` | `Program.cs` 74–84 |
| Menu key / TTL | `restaurant:{guid}:menu`, 60 s | `RestaurantsController.cs` 22, 31, 113 |
| Delete-on-write | after PATCH menu / restaurant, `RemoveAsync` | `RestaurantsController.cs` 157, 211, 241 |
| Live tracking | `IOrderTrackingBus` → `RedisOrderTrackingBus` channel `order:{id}`. `IsEnabled` is `IConnectionMultiplexer.IsConnected` | `RedisOrderTrackingBus.cs` 21, 23, 45 |
| Who publishes | Day 4 handler, **after** `SaveChanges` | `OrderStatusChangedTrackingHandler.cs` 16–21 |
| SSE | `GET /api/v1/orders/{id}/events` `text/event-stream` | `OrderTrackingController.cs` 28–35 |

**Why not `IDistributedCache`:** that abstraction is GET/SET only. The stampede lock (`SET NX`) and pub/sub need **the same** client. One multiplexer, three jobs (ADR-018).

**Menu miss path:** `GetOrSetAsync` → Redis GET → empty → `SET lock:… NX EX 5` → replica query (`_read.Restaurants.Include(Menu)`) → `SET` JSON + 60 s TTL → delete lock. **Hit path:** Redis GET → deserialize → **no SQL**.

### Same Redis commands, other languages

The **protocol** is Redis. Java and Node do not get a different cache pattern — they get a different **driver**.

| Job | Tadka (.NET) | Java | Node |
|---|---|---|---|
| Client library | **StackExchange.Redis** | **Lettuce** (Spring Data Redis default). **Jedis** is the older synchronous client | **ioredis**. **node-redis** (`redis` on npm) is the official client |
| Process-wide connection | `IConnectionMultiplexer` singleton | `StatefulRedisConnection` / Spring `RedisTemplate` | one Redis client singleton (do not `new` per request) |
| Cache-aside | `GET` → miss → DB → `SET key json EX 60` | same commands, same key shape | same |
| Stampede | `SET lock:{key} token NX EX 5` | `SET … NX EX` | `SET … NX EX` |
| Delete-on-write | `DEL restaurant:{id}:menu` | `DEL` | `DEL` |
| Live tracking | `PUBLISH` / `SUBSCRIBE` `order:{id}` | Lettuce pub/sub | `publish` / `subscribe` |

If you can run the `redis-cli` file, you can read any of those three codebases: look for GET/SET/DEL/NX/PUBLISH.

---

## 0. Fresh start (pre-class)

**Why wipe:** Day 5 volumes have no Redis. A leftover `tadka-redis` from another clone occupies the name. `down` without `-v` is usually fine for Redis (no volume), but Postgres replica still wants a clean pair when switching branches.

```powershell
git checkout day-06
docker compose down -v
docker rm -f tadka-postgres tadka-postgres-replica tadka-redis
docker compose up -d
docker compose ps
docker exec tadka-redis redis-cli PING
dotnet test Tadka.slnx          # 27 cases. Needs Docker.
dotnet run --project src/Tadka.Api
```

**What that does:** starts primary, replica, **and Redis**. `PING` is the Redis healthcheck you can see. `dotnet run` migrates the **primary** (same as Day 5); Redis has no schema.

**How you know you are ready:** three containers `(healthy)`. `PONG`. Tests **27/27**. API **5224**.

Set once in the terminal you will use for Redis + curl (these are PowerShell variables, not Docker):

```powershell
$RID  = "a1b2c3d4-0001-4000-8000-000000000001"
$ITEM = "b1b2c3d4-0001-4000-8000-000000000001"
$KEY  = "restaurant:${RID}:menu"
```

`$KEY` must expand to `restaurant:a1b2c3d4-0001-4000-8000-000000000001:menu`. `Write-Host $KEY` if unsure.

---

## 1. Beat 1 — cache-aside: miss, then hit (ADR-018)

### The story (say this before any command)

```
10,000 people open Meghana's menu
        │
        ▼
   Redis GET  restaurant:{id}:menu
        │
        ├─ HIT  → JSON in ~1 ms, DB never sees it
        └─ MISS → replica SQL, SET key EX 60, return
```

Day 5 made that SQL **fast**. Day 6 asks whether it should **run at all**. Cache only if **repeat + low writes + stale-OK**. Menu yes. Order status / payment **no**.

We **induce a miss** by deleting the key. We do not break the API.

### 1. BREAK — force a miss

**What we did:** delete the cache key so the next GET must hit the DB (replica) and refill Redis.

```powershell
# Remove the menu JSON from Redis. 1 = there was a key, 0 = already empty. Either is fine.
docker exec tadka-redis redis-cli DEL $KEY

# Prove the key is gone. Must be 0. If 1, $KEY did not match (variable not set / quoted wrong).
docker exec tadka-redis redis-cli EXISTS $KEY
```

**How we identified the miss is armed:** `EXISTS` **0**.

### 2. First GET — miss fills Redis

```powershell
# -o NUL hides JSON. time_total is end-to-end HTTP (DB + Redis SET on a miss).
curl.exe -s -o NUL -w "miss  HTTP %{http_code}  time=%{time_total}s`n" http://localhost:5224/api/v1/restaurants/$RID/menu

docker exec tadka-redis redis-cli EXISTS $KEY
docker exec tadka-redis redis-cli TTL $KEY
```

**What that GET does:** `RestaurantsController.GetMenu` → `GetOrSetAsync`. Redis GET empty → lock → `_read` replica query → `SET` JSON with 60 s TTL.

**How we identified the miss worked:**

| Output | Meaning |
|---|---|
| HTTP **200** | Menu still served (from DB this time) |
| `EXISTS` **1** | Cache-aside **wrote** Redis |
| `TTL` **~50–60** | Safety net (ADR-018). `-1` = SET without EX (wrong). `-2` = key gone |
| `time=` larger than the next call | Miss pays SQL. Capture cold ~**0.28 s** |

### 3. FIX / proof — second GET is a hit

```powershell
curl.exe -s -o NUL -w "hit   HTTP %{http_code}  time=%{time_total}s`n" http://localhost:5224/api/v1/restaurants/$RID/menu
```

**What it does:** same URL. Redis GET finds JSON. **No SQL.** `RedisCacheService.cs` 26–28.

**Fixed if:** HTTP **200**, `time=` much smaller (capture ~**0.023 s**). Your milliseconds will differ; **hit ≪ miss** is the lesson.

### 4. Scan-count proof — hits do not touch Postgres

The replica serves menu SQL on a miss (`_read`). If 20 GETs with a warm key still increment scans, the cache is not being used.

```powershell
# seq_scan + idx_scan on restaurant.menu_items (replica — that is who GetMenu queries).
$SQL = "select seq_scan+idx_scan from pg_stat_user_tables where schemaname='restaurant' and relname='menu_items';"
$B = docker exec tadka-postgres-replica psql -U tadka -d tadka -tAc $SQL
1..20 | ForEach-Object { curl.exe -s -o NUL http://localhost:5224/api/v1/restaurants/$RID/menu }
$A = docker exec tadka-postgres-replica psql -U tadka -d tadka -tAc $SQL
Write-Host "before=$B after=$A"
```

**How we identified it worked:** `before` and `after` are **equal** (delta **0**). If after > before: you `DEL`’d mid-loop, Redis was down, or `$RID` was wrong so every GET 404’d / missed.

---

## 2. Beat 2 — delete-on-write (invalidation)

### The story

TTL-only: a cook marks biryani unavailable, customers still see it for **up to 60 s**, then get 409 at checkout. So on **write**, we **delete** the key. Next GET is a miss and refills from DB. TTL is the **safety net** if a delete is missed — not the strategy.

We do **not** write-through (update Redis in the same request as SQL): if Redis SET succeeds and the DB transaction rolls back, the cache **lies**. Delete is safer (worst case is a miss).

### 1. BREAK — PATCH availability while the key exists

Need `EXISTS` 1 first (run Beat 1 if not).

```powershell
# Body is {"isAvailable":false}. Quote @ so PowerShell does not splat the path.
curl.exe -s -w "`nHTTP %{http_code}`n" -X PATCH http://localhost:5224/api/v1/restaurants/$RID/menu/$ITEM/availability -H "Content-Type: application/json" --data-binary "@docs/runbooks/menu-unavailable.json"

# Must be 0: RemoveAsync ran after SaveChanges.
docker exec tadka-redis redis-cli EXISTS $KEY
```

**What PATCH does:** updates `menu_items` on the **primary**, then `RemoveAsync` (`DEL $KEY`). `RestaurantsController.cs` 155–157.

**How we identified the issue we just fixed:** if we had skipped `RemoveAsync`, `EXISTS` would stay **1** and GET would still show biryani available. **Identified (good):** HTTP **204**, `EXISTS` **0**.

### 2. FIX — next GET repopulates

```powershell
curl.exe -s -o NUL -w "HTTP %{http_code} time=%{time_total}s`n" http://localhost:5224/api/v1/restaurants/$RID/menu
docker exec tadka-redis redis-cli EXISTS $KEY
```

**Fixed if:** HTTP **200**, `EXISTS` **1** again. JSON has biryani `isAvailable: false`.

Put the item back so later beats use a normal menu:

```powershell
curl.exe -s -o NUL -X PATCH http://localhost:5224/api/v1/restaurants/$RID/menu/$ITEM/availability -H "Content-Type: application/json" --data-binary "@docs/runbooks/menu-available.json"
```

**Never cache** order status or payment. Those fail the stale-OK test (Day 4 duplicate order / Day 5 read-your-writes).

---

## 3. Beat 3 — Redis down: same tool, two classifications

### The story

Redis is **one** process doing cache **and** the SSE backplane. When it dies, those two jobs must **fail differently**.

- Menu GET: **performance** dependency. App catches `RedisException` and hits the DB. Customer still sees the menu.
- SSE: **correctness** dependency for the stream. No backplane → do not pretend to stream. **503**.

Orders still **place** without Redis (Postgres).

### 1. BREAK — stop Redis, leave the API running

```powershell
# Stops tadka-redis. Does not kill Postgres or the API.
docker compose stop redis

curl.exe -s -o NUL -w "menu HTTP %{http_code} time=%{time_total}s`n" http://localhost:5224/api/v1/restaurants/$RID/menu
curl.exe -s -o NUL -w "sse  HTTP %{http_code}`n" http://localhost:5224/api/v1/orders/00000000-0000-0000-0000-000000000001/events
```

The SSE URL’s GUID need not exist: Redis is down, so the action returns 503 **before** it cares about the order.

**How we identified the two classifications:**

| Output | Meaning |
|---|---|
| menu HTTP **200** | `RedisCacheService` 62–66 fell back to SQL. Slower is OK. **500** would mean we treated Redis as required for reads |
| sse HTTP **503** | `OrderTrackingController` 31–35: `_bus.IsEnabled` is `IConnectionMultiplexer.IsConnected`. Redis stopped → not connected → 503. **200 + blank** would lie |

### 2. FIX — start Redis again

```powershell
docker compose start redis
docker exec tadka-redis redis-cli PING
```

**Fixed if:** `PONG`. Menu GET still 200 (now can hit again). SSE on a **real** order will stream (Beat 5) instead of 503.

---

## 4. Beat 4 — stampede lock (inspect, no 10k herd)

### The story

Cache-aside is fine until a **hot key expires**. At dinner rush, thousands of in-flight menu GETs miss in the same millisecond and **all** hit Postgres. For a moment the DB is busier than with **no** cache.

**Fix (ADR-019):** on miss, `SET lock:{key} {token} NX EX 5`. One winner runs SQL and fills Redis. Losers wait ~80 ms × 5 and re-GET. If still empty, they hit the DB (correctness over purity). Release `DEL`s the lock **only if the token is still ours**.

There is **no** “turn the lock off” lever in this branch. We **read the code** and maybe glimpse the key.

```powershell
# Usually empty: lock lives milliseconds. Empty is NOT "the lock is missing."
docker exec tadka-redis redis-cli KEYS lock:*
```

**How we identified it in code:** `RedisCacheService.cs` 31–33 `StringSetAsync(..., When.NotExists)`. Play the same primitive by hand in [`redis-cli.md` §3](../database/redis-cli.md).

**Honesty:** do not fake a `KEYS` hit. Hot-key (IPL, one restaurant) is **recognize today, solve later** (probabilistic early expiration / stale-while-revalidate — ADR-019 Revisit when).

---

## 5. Beat 5 — SSE live tracking (ADR-020)

### The story

Polling every 2 s is thousands of “nothing changed” SQLs. Tracking is **one-way** (server → phone) → **SSE**, not WebSocket. The stream lives on **one** API process. Two instances: PATCH lands on B, curl is connected to A → **silent drop** unless they share a channel.

**Backplane:** after `SaveChanges`, `OrderStatusChangedTrackingHandler` publishes to Redis channel `order:{id}`. The SSE action **subscribes** first, then writes `event: {Status}` lines until the client disconnects.

Pub/sub is fire-and-forget. Fine for “where is my biryani.” Not for payments. Replay/`Last-Event-ID` is **on the branch**, not this lecture.

### 1. BREAK — stream with no `-N` (optional, 10 seconds)

`curl` without `-N` **buffers**. Terminal A stays blank until you Ctrl+C. Students think SSE is broken.

### 2. FIX — two terminals, `-N` required

**Terminal A** — place an order, copy `"id"`, then stream (leave this running):

```powershell
# POST Priya + Meghana biryani. Copy the JSON "id" GUID. $ORDER is never set.
curl.exe -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"

# -N = no buffer. Paste the GUID. Stays open.
curl.exe -N http://localhost:5224/api/v1/orders/PASTE_ID/events
```

**What `-N` does:** disables curl’s output buffering so each SSE `event:` line prints when Redis publishes, not at disconnect.

**Terminal B** — same id, confirm the order:

```powershell
# PATCH Created → Confirmed. File body, not inline JSON.
curl.exe -s -w "`nHTTP %{http_code}`n" -X PATCH http://localhost:5224/api/v1/orders/PASTE_ID/status -H "Content-Type: application/json" --data-binary "@docs/runbooks/status-confirmed.json"
```

**What B does:** primary `SaveChanges` → domain event → `PublishAsync` on `order:{id}` (`RedisOrderTrackingBus.cs` 45). A is subscribed (`OrderTrackingController` 44–45).

**How we identified it worked:**

| Output | Meaning |
|---|---|
| A prints `event: Confirmed` (and a `data:` JSON line) | Backplane delivered. Not polling |
| B HTTP **204** | Status saved even if A is slow |
| A HTTP **503** | Redis still stopped from Beat 3. `compose start redis`, new stream |
| A stays blank | Forgot `-N`, or PASTE_ID mismatch, or Redis down |

Optional miniature (no API): [`redis-cli.md` §5](../database/redis-cli.md) `SUBSCRIBE` / `PUBLISH`.

---

## 6. Scale-out preview only (not the lecture)

The branch can start **three** API containers behind nginx (`--profile scale-out`). **Do not** teach 047–051. Three questions, then stop:

1. Where does the **cache** live? (Redis = shared. `Cache:Mode=InMemory` = each box lies.)
2. Who counts **rate limits**? (must be Redis, not per-process memory.)
3. Which box holds the **SSE** connection? (any box, **because** of the backplane.)

```powershell
docker compose --profile scale-out up -d
curl.exe -i http://localhost:8090/health
docker compose --profile scale-out down
```

Beats 4–10 of the break-kit are **homework**. Rate-limit depth is Day 11.

---

## Done when (Sunday)

- [ ] `PONG`; three containers healthy; 27/27
- [ ] Miss then hit (`time_total` down); 20 GETs **+0** replica scans
- [ ] PATCH availability → `EXISTS` 0, next GET → 1
- [ ] Redis stopped: menu **200**, SSE **503**
- [ ] Two-terminal SSE prints `event: Confirmed`

## Troubleshooting

| Symptom | What to do |
|---------|------------|
| `tadka-redis` name in use | `docker rm -f tadka-redis`; `docker compose up -d` |
| `EXISTS` stays 0 after GET | `$KEY` / `$RID` not set in **this** terminal. `Write-Host $KEY` |
| Scan count moved | You `DEL`’d or Redis was down — hits were misses |
| SSE blank | `curl.exe -N`. Redis `PONG`. 503 = Redis down (by design) |
| PATCH 400 | `@docs/runbooks/menu-unavailable.json` / `status-confirmed.json`, not inline JSON |
| Menu 500 with Redis down | You are not on `day-06` (fallback is this branch) |
| `KEYS lock:*` empty | Expected. Read the code; do not fake |

HTTP caching / ETag / gzip: weekday, `day6-caching-compression` source. Next: Week 4 payment brownout.
