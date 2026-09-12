# Redis CLI playground (Day 6)

Open `index.html` in a browser. Type commands in the `redis>` prompt. No npm, no server.

This is the **command gym** for Hour 1 (Redis fundamentals). It is not the cache-aside proof — that stays in `docs/runbooks/day-06.md` (curl the menu, then `EXISTS` / `TTL`).

## Start Redis (same container as class)

From the tadka repo root. Do **not** start a second Redis.

```powershell
docker compose up -d redis
docker exec tadka-redis redis-cli ping          # PONG
docker exec -it tadka-redis redis-cli           # interactive
```

If the name is already in use: `docker rm -f tadka-redis; docker compose up -d redis`.

Windows does not need `redis-cli` installed — `docker exec` is the client.

## How to use it

1. Open `index.html` (Docker can be down). Complete the labs in the simulator.
2. Start `tadka-redis`. Each lab has a **copy** button for the same command against the real container.
3. Last lab is the trap: do **not** cache order status.

## What this is

A teaching subset of the five Redis types, plus the Day-6 cache commands:

| Type | Commands to type | Tadka hook |
|---|---|---|
| string | `SET` `GET` `INCR` `TTL` | menu JSON blob |
| hash | `HSET` `HGET` `HGETALL` `HINCRBY` | field-level restaurant (not how Day 6 caches) |
| list | `RPUSH` `LRANGE` `LPOP` `LLEN` | `order:{id}:recent` replay buffer |
| set | `SADD` `SISMEMBER` `SINTER` | unique membership |
| sorted set | `ZADD` `ZRANGE` `ZSCORE` | ranking; GEO on Day 11 is this under the hood |

Also: `SET NX EX` (stampede lock), `KEYS` vs `SCAN`, `PUBLISH`.

## What this is not

- Not Redis 7. No persistence, Lua, Cluster, or `GEOADD` (that is Day 11).
- Not `hot-key-stampede-toy` (that counts DB queries on expiry).
- `KEYS` is fine on this laptop. It **blocks** Redis in production — use `SCAN`.
- Tadka Day 6 still caches the menu as a **string**. The hash lab is so you feel the other option.
