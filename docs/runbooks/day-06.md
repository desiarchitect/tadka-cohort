# Day 6 — Runbook: Redis Cache + Live Order Tracking

**Branch:** `day-06`  ·  **What's new:** Redis **cache-aside** on the menu (with a single-flight stampede lock and delete-on-write invalidation), and **live order tracking** via **Server-Sent Events over a Redis pub/sub backplane**. Now three containers: Postgres primary (5432) + replica (5433) + **Redis (6379)**.

> New here? Read [`README.md`](README.md). Windows PowerShell → use `curl.exe`.

## 1. Run it (postgres + replica + redis)

```bash
git checkout day-06
docker compose up -d
docker exec tadka-redis redis-cli ping        # PONG
docker compose ps                              # postgres, postgres-replica, redis all healthy
dotnet run --project src/Tadka.Api
```

Handy variables:
```bash
RID=a1b2c3d4-0001-4000-8000-000000000001        # Meghana
KEY=restaurant:$RID:menu
ITEM=b1b2c3d4-0001-4000-8000-000000000001       # Chicken Biryani
```

## 2. Cache-aside: miss → hit, and the DB goes untouched (ADR-018)

```bash
docker exec tadka-redis redis-cli DEL $KEY                     # start cold
docker exec tadka-redis redis-cli EXISTS $KEY                  # 0
curl -s -o /dev/null http://localhost:5224/api/v1/restaurants/$RID/menu    # 1st call: MISS → DB → populates Redis
docker exec tadka-redis redis-cli EXISTS $KEY                  # 1 (cached)
docker exec tadka-redis redis-cli TTL $KEY                     # ~60 (seconds)
curl -s -o /dev/null http://localhost:5224/api/v1/restaurants/$RID/menu    # 2nd call: HIT (served from Redis)
```
Prove the DB is **untouched** on cache hits (scan count flat across repeated GETs, measured on the replica):
```bash
SQL="select seq_scan+idx_scan from pg_stat_user_tables where schemaname='restaurant' and relname='menu_items';"
B=$(docker exec tadka-postgres-replica psql -U tadka -d tadka -tAc "$SQL")
for i in $(seq 1 20); do curl -s -o /dev/null http://localhost:5224/api/v1/restaurants/$RID/menu; done
A=$(docker exec tadka-postgres-replica psql -U tadka -d tadka -tAc "$SQL")
echo "menu_items DB scans: before=$B after=$A  (delta 0 = 20 cached GETs hit Redis, not the DB)"
```
> Captured on a dev laptop: menu GET **~282 ms (miss → replica DB) → ~23 ms (hit → Redis)**.

## 3. Invalidation: delete-on-write (ADR-018)

Change the menu → the cache key is deleted immediately, so customers never see a stale price:
```bash
curl -s -o /dev/null -X PATCH http://localhost:5224/api/v1/restaurants/$RID/menu/$ITEM/availability \
  -H "Content-Type: application/json" -d '{"isAvailable":false}'
docker exec tadka-redis redis-cli EXISTS $KEY                  # 0  (busted on write)
curl -s -o /dev/null http://localhost:5224/api/v1/restaurants/$RID/menu
docker exec tadka-redis redis-cli EXISTS $KEY                  # 1  (repopulated, fresh)
# restore availability:
curl -s -o /dev/null -X PATCH http://localhost:5224/api/v1/restaurants/$RID/menu/$ITEM/availability -H "Content-Type: application/json" -d '{"isAvailable":true}'
```
> TTL (~60 s) is the safety net if a delete is ever missed; delete-on-write is the strategy. We **never** cache order status/payment (read-your-writes — stale there → duplicate orders).

## 4. Redis is a *performance* dependency, not a correctness one

```bash
docker compose stop redis
curl -s -o /dev/null -w "menu with Redis DOWN: %{http_code}\n" http://localhost:5224/api/v1/restaurants/$RID/menu   # still 200 (no-op cache → DB), just slower
docker compose start redis
```

## 5. Live order tracking: SSE over the Redis backplane (ADR-020)

Open a stream for an order in **one terminal**:
```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" \
  -d '{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"'$RID'","items":[{"menuItemId":"'$ITEM'","quantity":1}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.9,"longitude":77.7}}' | sed -E 's/.*"id":"([^"]+)".*/\1/')
curl -N http://localhost:5224/api/v1/orders/$ORDER/events        # streams; leave it open (Windows: curl.exe -N)
```
In a **second terminal**, advance the status:
```bash
curl -s -o /dev/null -X PATCH http://localhost:5224/api/v1/orders/$ORDER/status -H "Content-Type: application/json" -d '{"status":"Confirmed"}'
curl -s -o /dev/null -X PATCH http://localhost:5224/api/v1/orders/$ORDER/status -H "Content-Type: application/json" -d '{"status":"Preparing"}'
```
The first terminal prints, live:
```
event: Created
data: {"orderId":"…","status":"Created", …}
event: Confirmed
data: {"orderId":"…","status":"Confirmed", …}
event: Preparing
data: {"orderId":"…","status":"Preparing", …}
```
> Each status change is published to Redis channel `order:{id}`; the SSE endpoint (subscribed) streams it — so an update from *any* app instance reaches the connection held by *another* instance. Push, not poll.

## 6. Scale-out: 3 replicas + nginx LB, HTTP efficiency, rate limiting, edge cache, signed URLs (Beats 4-10)

```bash
docker compose --profile scale-out up -d     # builds src/Tadka.Api/Dockerfile, starts api-1/2/3 + nginx on :8090
curl -i http://localhost:8090/health          # X-Tadka-Instance shows which replica answered
docker stop tadka-api-2-1                     # kill one — requests keep succeeding (nginx evicts + retries)
docker start tadka-api-2-1                    # rejoins the rotation within ~20s
```

- **Compression + ETag (ADR-048):** `curl -H "Accept-Encoding: gzip" -D - http://localhost:8090/api/v1/restaurants` → `Content-Encoding: gzip`; repeat with `If-None-Match: <etag>` → `304`.
- **Rate limiting (ADR-049):** default 120/min, Redis-shared across all 3 replicas. `RateLimit:Algorithm=FixedWindow|SlidingWindow`, `RateLimit:WindowSeconds` (short window for a fast classroom demo of the boundary burst).
- **State divergence (ADR-047):** set `Cache:Mode=InMemory` on the 3 replicas (see `scripts/` or a compose override) to watch per-replica cache divergence after a price update — the default (Redis) mode stays consistent.
- **Edge cache (ADR-050):** `X-Edge-Cache: MISS|HIT` header on `/api/v1/restaurants*` responses. 30s TTL.
- **Signed URLs (ADR-050):** `POST /api/v1/orders/{id}/invoice/sign` → time-limited link; `GET .../invoice?sig=&exp=`.
- **SSE reconnect (ADR-051):** reconnect with `Last-Event-ID: <seq>` to replay missed status transitions.

Full click-by-click sequences with captured numbers: `cohort-prep/day-06/break-kit-day-06.md` (Beats 4-10 + Bonus).

```bash
docker compose --profile scale-out down       # tear down when done (plain `docker compose down` for the base stack)
```

## 7. Run the tests

```bash
dotnet test      # 27/27 — the shared factory (ETag tests included) runs Redis-free and deterministic;
                  # RateLimiterTests spin up their own dedicated Redis Testcontainer, so no scale-out stack needed
```

## ✅ Done when

- [ ] `redis-cli ping` → PONG; all three containers healthy.
- [ ] Menu GET: 1st call caches the key (`EXISTS`→1, `TTL`→~60); repeated GETs add **0** DB scans (served from Redis); hit is much faster than miss.
- [ ] A menu write deletes the key (`EXISTS`→0), and the next GET repopulates it fresh.
- [ ] With Redis **stopped**, the menu still returns `200` (no-op fallback).
- [ ] The SSE stream prints `Created → Confirmed → Preparing` as you PATCH the status.
- [ ] `docker compose --profile scale-out up -d`: kill one replica mid-load, 0 client-visible failures; it rejoins after restart.
- [ ] Gzip/brotli `Content-Encoding` present; matching `If-None-Match` → `304`.
- [ ] 150 rapid requests → some `429`s with a real `Retry-After`, shared across all 3 replicas.
- [ ] `dotnet test` → 27/27.

## Troubleshooting

- **`tadka-redis` name already in use:** `docker rm -f tadka-redis` then `docker compose up -d`.
- **SSE prints nothing:** ensure you used `curl -N` (no buffering); on Windows PowerShell use `curl.exe -N`. Confirm Redis is up (`ping` → PONG) — without Redis the SSE endpoint returns `503`.
- **Reset everything:** `docker compose down -v && docker compose up -d`, then `dotnet run`.

That's Weeks 1–3. Next up (Week 4): the payment-gateway brownout that forces the modular monolith + the first service extraction.
