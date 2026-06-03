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

## 6. Run the tests

```bash
dotnet test      # 19/19 — tests run with the no-op cache (no Redis dependency), so they stay deterministic
```

## ✅ Done when

- [ ] `redis-cli ping` → PONG; all three containers healthy.
- [ ] Menu GET: 1st call caches the key (`EXISTS`→1, `TTL`→~60); repeated GETs add **0** DB scans (served from Redis); hit is much faster than miss.
- [ ] A menu write deletes the key (`EXISTS`→0), and the next GET repopulates it fresh.
- [ ] With Redis **stopped**, the menu still returns `200` (no-op fallback).
- [ ] The SSE stream prints `Created → Confirmed → Preparing` as you PATCH the status.
- [ ] `dotnet test` → 19/19.

## Troubleshooting

- **`tadka-redis` name already in use:** `docker rm -f tadka-redis` then `docker compose up -d`.
- **SSE prints nothing:** ensure you used `curl -N` (no buffering); on Windows PowerShell use `curl.exe -N`. Confirm Redis is up (`ping` → PONG) — without Redis the SSE endpoint returns `503`.
- **Reset everything:** `docker compose down -v && docker compose up -d`, then `dotnet run`.

That's Weeks 1–3. Next up (Week 4): the payment-gateway brownout that forces the modular monolith + the first service extraction.
