# Day 6 changelog (since Day 5)

`git checkout day-06`. Previous branch: `day-05`.

## We learned

- Cache **eligibility**: high read:write **and** staleness is tolerable. Menu yes. Order status / payment **never**.
- **Cache-aside:** GET Redis → miss → DB → SET EX 60. Delete-on-write + TTL safety net.
- **Stampede:** `SET lock NX EX` so one refresher hits the DB. Release with **Lua compare-and-delete** (not GET-then-DEL — that race can drop a new owner's lock).
- **SSE** over Redis pub/sub for live order status. Fire-and-forget — not for money.
- Redis down: menu **200** (performance dep, fall through to DB). SSE **503** (correctness for the stream). Same tool, two classifications.

## Architecture

- New datastore: **Redis** `:6379` (`tadka-redis`). Postgres remains source of truth.
- `ICacheService` in front of StackExchange.Redis; no connection string → `NullCacheService`.
- `IOrderTrackingBus` → Redis pub/sub, or no-op + 503 if Redis unset.

## Code vs Day 5

| Area | What changed |
|---|---|
| `Infrastructure/Caching/*` | `ICacheService`, `RedisCacheService` (Lua lock release), `NullCacheService` |
| `RestaurantsController` | Menu GET via cache-aside; writes `RemoveAsync` |
| `Infrastructure/Realtime/*` | Redis SSE backplane |
| `OrderTrackingController` | 503 if Redis down |
| `docker-compose.yml` | `redis` service |
| `toydemo/day-06-cache-realtime/` | Stampede toy, rate-limiter toy, **redis-cli playground** |
| `docs/database/redis-cli.md` | Command walkthrough including hashes/lists/sets/zsets |

ADRs **018, 019, 020**.
