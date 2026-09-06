# Redis CLI — student walkthrough (Day 6)

Redis is an in-memory store. Tadka talks to it with **StackExchange.Redis** (the app). You talk to it with **`redis-cli`** (this file). The **commands** are the same either way.

Run every command from the repo root, with `tadka-redis` up (`docker compose up -d` on branch `day-06`). You do **not** need `redis-cli` installed on Windows.

```powershell
docker exec tadka-redis redis-cli PING
# PONG
```

Interactive session (Ctrl+C to leave):

```powershell
docker exec -it tadka-redis redis-cli
```

Inside `redis-cli`, type commands without the `docker exec …` prefix. This file uses the one-shot form so you can copy from PowerShell.

**Keys are strings.** Tadka’s menu key looks like `restaurant:{guid}:menu`. Values for the menu are **JSON strings**, not a RedisJSON document.

## Cheat sheet

| Command | What it does | Tadka uses it for |
|---|---|---|
| `PING` | Health. `PONG` = Redis is up | compose healthcheck |
| `SET k v` | Write a string | cache fill (`StringSetAsync`) |
| `GET k` | Read a string | cache hit |
| `DEL k` | Delete | delete-on-write; demo miss |
| `EXISTS k` | `1` or `0` | prove miss vs hit |
| `TTL k` | Seconds until expiry. `-1` no TTL, `-2` key gone | menu TTL ~60 |
| `EXPIRE k n` | Set TTL on an existing key | (TTL is usually set with SET) |
| `SET k v NX EX n` | Set **only if missing**, expire in n seconds | stampede lock (`When.NotExists`) |
| `KEYS pattern` | List keys. **Demo only** — blocks Redis | `KEYS lock:*` |
| `SCAN 0 MATCH pattern` | List keys without blocking | production-safe listing |
| `TYPE k` | `string` / `list` / `none` | menu is a string; replay buffer is a list |
| `SUBSCRIBE ch` | Block and print messages | SSE backplane (app side) |
| `PUBLISH ch msg` | Send to all subscribers of `ch` | status change → `order:{id}` |

Tadka keys you will see in class:

| Key | Type | Who writes it |
|---|---|---|
| `restaurant:{id}:menu` | string (JSON) | `RedisCacheService` on a miss |
| `lock:restaurant:{id}:menu` | string (token) | same, ~5 s, usually gone before you look |
| `order:{id}` | pub/sub **channel** (not a key) | `RedisOrderTrackingBus.PublishAsync` |
| `order:{id}:recent` | list | replay buffer (on the branch, **not** Sunday lecture) |
| `order:{id}:seq` | string (counter) | same |

---

## 1. Alive

```powershell
docker exec tadka-redis redis-cli PING
```

**Does:** asks Redis if the process is accepting commands.  
**Expect:** `PONG`. Empty / connection refused = container down (`docker compose ps`).

## 2. Strings — the cache primitive

```powershell
docker exec tadka-redis redis-cli SET demo:hello "biryani"
docker exec tadka-redis redis-cli GET demo:hello
docker exec tadka-redis redis-cli EXISTS demo:hello
docker exec tadka-redis redis-cli TTL demo:hello
```

**Does:**

| Line | Meaning |
|---|---|
| `SET` | write key `demo:hello` = `biryani`. Replaces any old value. |
| `GET` | read it back. `(nil)` = miss. |
| `EXISTS` | `1` = key is there, `0` = not. |
| `TTL` | `-1` = no expiry (this SET did not pass `EX`). `-2` = key does not exist. |

Give it a TTL (this is the menu pattern: value + 60 s safety net):

```powershell
docker exec tadka-redis redis-cli SET demo:hello "biryani" EX 10
docker exec tadka-redis redis-cli TTL demo:hello
```

Wait 11 seconds, `GET` again → `(nil)`, `EXISTS` → `0`. The key **evicted itself**. Tadka’s menu uses the same idea with 60 s.

Clean up:

```powershell
docker exec tadka-redis redis-cli DEL demo:hello
```

`DEL` returns the number of keys removed (`1` or `0`).

## 3. SET NX EX — the stampede lock primitive

`NX` = set **only if the key does not exist**. Atomic. Two callers cannot both win.

```powershell
docker exec tadka-redis redis-cli SET lock:demo token-A NX EX 5
docker exec tadka-redis redis-cli SET lock:demo token-B NX EX 5
docker exec tadka-redis redis-cli GET lock:demo
```

**Expect:** first SET → `OK` (you are the refresher). Second SET → `(nil)` (someone else holds it). `GET` → `token-A` (not B). After ~5 s the lock expires on its own — if the winner crashes, the next caller can take over.

Tadka: `RedisCacheService` does `StringSetAsync(lockKey, token, 5s, When.NotExists)` then `GET` the cache, then `DEL` the lock **only if the stored token is still ours**.

## 4. KEYS vs SCAN

```powershell
docker exec tadka-redis redis-cli SET a:1 1
docker exec tadka-redis redis-cli SET a:2 2
docker exec tadka-redis redis-cli KEYS a:*
docker exec tadka-redis redis-cli SCAN 0 MATCH a:*
docker exec tadka-redis redis-cli DEL a:1 a:2
```

`KEYS a:*` lists matching keys **in one shot**. Fine for this laptop. In production it **blocks** Redis while it walks every key — dinner-rush suicide. `SCAN` walks in small cursor steps. Class demos may use `KEYS`; never ship `KEYS *`.

## 5. Pub/sub — the SSE backplane in miniature

**Terminal A** (stays open; needs `-it`):

```powershell
docker exec -it tadka-redis redis-cli SUBSCRIBE order:demo
```

You should see `subscribe` / `order:demo` / `1`.

**Terminal B:**

```powershell
docker exec tadka-redis redis-cli PUBLISH order:demo "Confirmed"
```

**Expect** in A: a `message` line with `Confirmed`. That is what `GET /orders/{id}/events` is doing: the API **subscribes** to `order:{id}`; PATCH Confirmed **publishes** after the row is saved.

Ctrl+C in A to stop. Pub/sub is **fire-and-forget**: if A was not subscribed, the message is gone. Fine for “where is my biryani”; not fine for payments.

## 6. Inspect Tadka’s real keys (after the runbook miss→hit)

Meghana = `a1b2c3d4-0001-4000-8000-000000000001`.

```powershell
docker exec tadka-redis redis-cli EXISTS restaurant:a1b2c3d4-0001-4000-8000-000000000001:menu
docker exec tadka-redis redis-cli TTL restaurant:a1b2c3d4-0001-4000-8000-000000000001:menu
docker exec tadka-redis redis-cli TYPE restaurant:a1b2c3d4-0001-4000-8000-000000000001:menu
docker exec tadka-redis redis-cli GET restaurant:a1b2c3d4-0001-4000-8000-000000000001:menu
```

After a menu GET: `EXISTS` 1, `TTL` around 60, `TYPE` `string`, `GET` a JSON array of menu items. `GET` before any menu call: `EXISTS` 0.

`KEYS lock:*` after a miss is **usually empty** — the lock lasts ~5 s and is deleted when the refresh finishes. Do not treat empty as “the lock is missing from the code.”

## Gotchas

| What you saw | Why |
|---|---|
| `PING` fails | Redis container not running, or you exec’d `tadka-postgres` |
| `GET` `(nil)` after you just cached | Wrong key (typo / different restaurant GUID) |
| `TTL` `-1` | You `SET` without `EX` |
| `TTL` `-2` | Key already expired or never existed |
| `SUBSCRIBE` prints nothing | You used `docker exec` **without** `-it`, or published a **different** channel name |
| PowerShell ate `$KEY` | Unquoted `$KEY` is expanded (good). Single-quoted `'$KEY'` is the literal dollar word (bad). |

Full Day 6 demo (miss/hit, delete-on-write, Redis down, SSE): [`docs/runbooks/day-06.md`](../runbooks/day-06.md).
