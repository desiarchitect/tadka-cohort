# Day 7 — Runbook (you run this)

You are on branch `day-07`. What changed since Day 6: [`docs/changelog.md`](../changelog.md).

Today you **wire payment** and watch it fail first, then you fix it in two steps. You also see what Redis does in production (down, replica, Cluster, Sentinel).

| | Naive (broken) | Fix 1 | Fix 2 (shipped) |
|---|---|---|---|
| What you change | Charge **inside** `POST /orders`, Slow gateway, no real timeout | Same, but Polly **timeout 2 s** + **bulkhead 10** | Charge **off** the request path (`Async`) |
| What you should see | POST takes **~8–10 s**, still 201 | POST **~3 s**, order **Cancelled** | POST **~20 ms**, status `Created` |

Fill those times yourself as you go. Captured on one laptop: **~8.8 s → ~2.9 s → ~20 ms**.

There is **no** Kafka and **no** Payment HTTP service today. That is Day 8.

---

## Before you type anything

**Windows PowerShell**

- Use `curl.exe`, not `curl` (`curl` is `Invoke-WebRequest`).
- Env vars use **two** underscores: `Payment__Mode`. One `_` does nothing and does not error.
- `$env:…` lasts only in **this** terminal. A running `dotnet run` does **not** pick up new env — Ctrl+C and start it again.
- Always `--launch-profile http` so you get port **5224** and the Development connection string.

**How to read a POST in this file**

| Flag | What it does |
|---|---|
| `-s` | no progress bar |
| `-o NUL` | hide the JSON body (timing-only) |
| `-w "…%{http_code} %{time_total}s"` | print status and seconds |
| `--data-binary "@docs/runbooks/place-order.json"` | `@` means “read this file” |
| `--max-time 20` | stop if the brownout hangs longer than 20 s |

`curl` **`000`** = nothing is listening (API down). That is **your setup**, not the lesson.

| | |
|---|---|
| API | `http://localhost:5224` |
| Order JSON | `docs/runbooks/place-order.json` |
| Meghana restaurant id | `$RID = "a1b2c3d4-0001-4000-8000-000000000001"` |
| Payments | schema `payment` on the **same** Postgres as orders |

**Env you will set** (Ctrl+C the API each time)

| Variable | §2 brownout | §3 timeout | §3b bulkhead | §4 / shipped |
|---|---|---|---|---|
| `Payment__Mode` | `Synchronous` | `Synchronous` | `Synchronous` | `Async` |
| `Payment__Gateway__Behavior` | `Slow` | `Slow` | `Slow` | `Slow` then `Fast` |
| `Payment__TimeoutSeconds` | `30` | **`2`** | **`30`** | `2` |
| `Payment__MaxConcurrentCharges` | `1000` | `10` | **`10`** | `10` |
| `RateLimit__PerMinute` | default 120 | default 120 | **`1000`** | default 120 |

Shipped `appsettings.Development.json` is **Async + Fast + timeout 2 + cap 10**. A new terminal with **no** `$env:Payment__*` is shipped.

What the code is doing:

```
POST /orders
    → save Order (Created)
    → MediatR OrderPlaced
         ├─ Synchronous: charge NOW (Polly around the fake gateway)
         └─ Async: put on a Channel; PaymentProcessor charges in the background
    → HTTP returns  (async: ms; sync: waits for the gateway)
```

---

## 0. Start

```powershell
git checkout day-07
docker compose down -v
docker rm -f tadka-postgres tadka-postgres-replica tadka-redis
docker compose up -d
dotnet test Tadka.slnx
# new terminal — no Payment__* env
dotnet run --project src/Tadka.Api --launch-profile http
```

| Command | What it does |
|---|---|
| `down -v` | wipe DB volumes (clean start) |
| `up -d` | Postgres `5432`, replica `5433`, Redis `6379` |
| `dotnet run --launch-profile http` | migrate **core** then **payment**, listen **5224** |

You are ready when `curl.exe http://localhost:5224/health` prints **200**.

Prove Payment has its own schema and migration history:

```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT tablename FROM pg_tables WHERE schemaname='payment' ORDER BY 1;"
```

You should see `payments` **and** `__EFMigrationsHistory`.

---

## 0b. Redis in production

Day 6 was commands. Here you see **what happens when Redis dies**, then **replica vs Cluster vs Sentinel**.

Tadka still uses **one** Redis: `tadka-redis` on `6379`. The HA boxes are a **toy** (`toydemo/day-07-redis-ha/`). You do **not** point Tadka at Cluster or Sentinel today.

Background: [`docs/learn/redis-in-production.md`](../learn/redis-in-production.md).

### If you already ran the toy (failover / killed a node)

Leftover state makes Sentinel show the **same** IP before and after `stop`. Wipe the toy only (Tadka Redis/Postgres stay up):

```powershell
docker compose -f toydemo/day-07-redis-ha/docker-compose.yml down -v
docker compose -f toydemo/day-07-redis-ha/docker-compose.yml up -d
```

Wait ~10 s. If `CLUSTER NODES` is empty: `docker start tadka-redis-c-init`.

Health must be **200**. Then:

```powershell
$RID = "a1b2c3d4-0001-4000-8000-000000000001"
```

### 0b.1 Redis down — three different answers

**What you are doing:** stop Tadka’s Redis. Menu should still work (slower). Live tracking should fail honestly. Placing an order should still work. Starting Redis at the end is a **reset**, not HA.

```powershell
# Stop Tadka Redis only (not the HA toy).
docker compose stop redis

# Cache is gone → SQL. You want HTTP 200. First hit can be 5-12 s — wait.
# 000 = API down. 500 = this branch has no fallback.
curl.exe -s -o NUL -w "menu %{http_code} %{time_total}s`n" http://localhost:5224/api/v1/restaurants/$RID/menu

# Live tracking needs Redis pub/sub. You want HTTP 503 and body "Live tracking requires Redis".
# 000 = curl got no status (old API hung). Pull, restart API, Redis still stopped, try again.
curl.exe -s -o NUL -w "sse  %{http_code}`n" --max-time 5 http://localhost:5224/api/v1/orders/00000000-0000-0000-0000-000000000001/events

# Orders use Postgres. You want HTTP 201.
curl.exe -s -o NUL -w "order %{http_code}`n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"

# Reset. This is not HA.
docker compose start redis
docker exec tadka-redis redis-cli PING
# PONG
```

| Result | Meaning |
|---|---|
| menu **200** | cache is a **performance** dependency — app stays correct, slower |
| sse **503** | the stream is a **correctness** dependency — fail honest |
| order **201** | money is not in Redis |

### 0b.2 Replica — copy, not failover

**What you are doing:** write on the master, read on the replica, **kill the writer**. The copy still has the key. Anything still aimed at the master is **down**. `docker start` is a **reset**. The real failover is §0b.4.

```powershell
# Write on the only writer.
docker exec tadka-ha-master redis-cli SET demo:ha namaste

# Read the copy. You want: namaste. This only proves replication.
docker exec tadka-ha-replica redis-cli GET demo:ha

# Confirm it is a follower. You want: role:slave
docker exec tadka-ha-replica redis-cli INFO replication

# THE FAILURE: kill the writer.
docker stop tadka-ha-master

# Copy survived. You want: namaste.
# THE FAIL that is not fixed yet: an app on host port 6380 (master) is dead. Copy != HA.
docker exec tadka-ha-replica redis-cli GET demo:ha

# Reset. Not the fix.
docker start tadka-ha-master
```

If GET is empty: wait 2 s and retry (replica not ready). If the container is missing: toy compose is not up.

### 0b.3 Cluster — sharding, not HA

**What you are doing:** `MOVED` is Cluster working (the key lives on another node). Then you **kill one shard** that has **no replica**. Those slots are **gone**. `docker start c2` is a **reset**. HA would be `--cluster-replicas 1`.

```powershell
# You want three masters, IPs 172.28.0.21-23.
docker exec tadka-redis-c1 redis-cli CLUSTER NODES

# Write without following redirects. You want: (error) MOVED … 172.28.0.2x:6379
# That is not a bug — that is sharding.
docker exec tadka-redis-c1 redis-cli SET user:1 a

# -c follows MOVED. You want: OK then a. Must be docker exec (MOVED is a Docker IP).
docker exec tadka-redis-c1 redis-cli -c SET user:1 a
docker exec tadka-redis-c1 redis-cli -c GET user:1

# THE FAILURE: kill one shard.
docker stop tadka-redis-c2

# Those slots had 0 replicas. You want: CLUSTERDOWN or error. The keyspace is gone (unlike the replica copy).
docker exec tadka-redis-c1 redis-cli -c GET user:1

# Reset. This is not --cluster-replicas 1.
docker start tadka-redis-c2
```

If `CLUSTER NODES` is empty **before** you killed a node: `docker start tadka-redis-c-init`.

### 0b.4 Sentinel — this is the failover (the fix)

**What you are doing:** the **same** `docker stop` as replica, but Sentinel **changes the writer**. That is HA.

**Before you stop anything:** `get-master-addr` **must** be `172.28.0.10`. If it is already `172.28.0.11`, you already failed over — `down -v` then `up -d`. Stopping `tadka-ha-master` then changes nothing.

```powershell
# Who is the writer? You want: 172.28.0.10
docker exec tadka-ha-sentinel redis-cli -p 26379 SENTINEL get-master-addr-by-name mymaster

# Same failure as replica.
docker stop tadka-ha-master
Start-Sleep -Seconds 8

# THE FIX: writer address changed. You want: 172.28.0.11
# (Not “GET still namaste” — that was the copy. Here the ROLE moved.)
docker exec tadka-ha-sentinel redis-cli -p 26379 SENTINEL get-master-addr-by-name mymaster

# Confirm. You want: role:master
docker exec tadka-ha-replica redis-cli INFO replication

# Reset. Old master may come back as a replica.
docker start tadka-ha-master
```

| After you `stop` the master | Replica (§0b.2) | Sentinel (§0b.4) |
|---|---|---|
| Data | Copy still `GET`s | Copy still `GET`s |
| Writer | Still the dead host | **Address becomes `.11`** |
| App with hardcoded host | Down | Still down unless it talks to **Sentinel** (`:26379`) |

Tadka’s config is still `localhost:6379`, so Tadka would **not** follow this failover. ElastiCache “primary endpoint” is Sentinel (or Cluster+replicas) **managed**.

If `.10` does not become `.11` in 10 s: see `toydemo/day-07-redis-ha/sentinel.conf`. Do not block the payment labs on this.

### 0b.5 Same seat, two countries (no command)

This is **not** Cluster. Cluster is one region. Mumbai Redis and London Redis are two caches. Both `GET seat:12A` = free → double booking.

**Fix:** one inventory writer — unique `(flight, seat)`, `UPDATE … WHERE free RETURNING`, hold with TTL. Regional Redis may cache the **map**. The book click always hits the seat service.

Tadka: cache the **menu**. Never cache **order status**.

---

## 1. Shipped path — async + fast

**What you are proving:** “order placed” does not wait for the bank. POST returns **`Created` in tens of milliseconds**. `Confirmed` comes on a **later GET**, not on this POST.

**Before this POST** the API must be shipped config: **no** `$env:Payment__*` in that terminal.

If you just ran Redis labs (`docker compose stop redis`) or already set `Payment__Mode=Synchronous` / `Slow`:

```powershell
docker compose start redis
docker exec tadka-redis redis-cli PING
# PONG

# Ctrl+C the running API
# Open a NEW PowerShell (old $env:Payment__* is still in the old window)

git checkout day-07
dotnet run --project src/Tadka.Api --launch-profile http
```

Wait until `curl.exe http://localhost:5224/health` is **200**. Then POST.

If your POST is **~9 s** and `"status":"Confirmed"` **in the same response**, you are still on **Synchronous + Slow** (the brownout). That is §2, not this step. Restart as above.

```powershell
curl.exe -s -w "`nHTTP %{http_code} time=%{time_total}s`n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
```

Copy `"id"` and `"status"`. First POST after boot can be ~0.8 s (JIT). **Second** POST is the number you want: HTTP **201**, `"Created"`, **~20 ms**.

Wait 2 s:

```powershell
curl.exe -s http://localhost:5224/api/v1/orders/PASTE_ID
```

You want `"status":"Confirmed"`.

```powershell
@'
SELECT "Status", "FailureReason", "CompletedAt" FROM payment.payments ORDER BY "CreatedAt" DESC LIMIT 1;
'@ | docker exec -i tadka-postgres psql -U tadka -d tadka
```

You want `Completed` and an empty `FailureReason`. (Pipe SQL on stdin. `psql -c "SELECT Status"` loses the quotes.)

Optional: `curl.exe -N http://localhost:5224/api/v1/orders/PASTE_ID/events` **before** POST — you should see `Created` then `Confirmed`.

---

## 2. Break — brownout (sync + slow, no real timeout)

**What is broken:** payment runs **inside** `POST /orders`. The fake gateway sleeps 8 s. Checkout holds a thread and a DB connection for ~9 s.

Ctrl+C the API. **Same** terminal:

```powershell
$env:Payment__Mode = "Synchronous"
$env:Payment__Gateway__Behavior = "Slow"
$env:Payment__Gateway__SlowDelaySeconds = "8"
$env:Payment__TimeoutSeconds = "30"
$env:Payment__MaxConcurrentCharges = "1000"
dotnet run --project src/Tadka.Api --launch-profile http
```

Other terminal:

```powershell
curl.exe -s -o NUL -w "BROWN HTTP %{http_code} time=%{time_total}s`n" --max-time 20 -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
```

**You want:** HTTP **201**, **time ≈ 8–10 s**. That is the brownout working. **500** = wrong commit.

---

## 3. Fix 1 — timeout 2 s (one call)

**What you fix:** still synchronous, but Polly **cuts the call at 2 s**. The order **cancels** instead of hanging ~9 s.

**What this is not:** the bulkhead (how many Slow calls at once) — that is §3b.

Ctrl+C. Overwrite env:

```powershell
$env:Payment__Mode = "Synchronous"
$env:Payment__Gateway__Behavior = "Slow"
$env:Payment__TimeoutSeconds = "2"
$env:Payment__MaxConcurrentCharges = "10"
dotnet run --project src/Tadka.Api --launch-profile http
```

Same POST curl as §2.

**You want:** HTTP **201**, **time ≈ 3 s**, JSON `"status":"Cancelled"`. Payment row: `Failed`, `TimeoutRejectedException` … `'00:00:02'`.

The customer used to succeed slowly; now they fail quickly. That is the trade-off. Timeout bounds **one** call.

---

## 3b. Fix 1b — bulkhead: 10 succeed, 100 → ~10

§3 proved **timeout** (one Slow call). This proves **how many** Slow charges may run at once.

**Succeed** means `payment.Status = Completed`, **not** HTTP 201 vs 429. Every POST still **creates the order** (201).

**Why restart (timeout 2 → 30):** Slow sleeps **8 s**. Timeout **2** would **fail** the ten slot-holders (`TimeoutRejectedException`, zero Completed). Timeout **30** lets them finish. Cap stays **10**.

| Env | Value | Why |
|---|---|---|
| `Payment__Mode` | `Synchronous` | you feel 8 s vs ms |
| `Payment__Gateway__Behavior` | `Slow` | holds a slot ~8 s. **Fast** recycles slots → more than 10 Completed |
| `Payment__TimeoutSeconds` | **`30`** | must outlive 8 s |
| `Payment__MaxConcurrentCharges` | **`10`** | 11th concurrent charge is rejected **now** |
| `RateLimit__PerMinute` | **`1000`** | leftover limiter; default 120 returns HTTP **429** and looks like the bulkhead |

Ctrl+C.

```powershell
$env:Payment__Mode = "Synchronous"
$env:Payment__Gateway__Behavior = "Slow"
$env:Payment__TimeoutSeconds = "30"
$env:Payment__MaxConcurrentCharges = "10"
$env:RateLimit__PerMinute = "1000"
dotnet run --project src/Tadka.Api --launch-profile http
```

Wait for health **200**:

```powershell
curl.exe -sS -o NUL -w "%{http_code}`n" http://localhost:5224/health
```

From the **repo root**:

```powershell
.\docs\demo-scripts\06-bulkhead-burst.ps1 -Count 10
.\docs\demo-scripts\06-bulkhead-burst.ps1 -Count 100
```

The script checks `/health`, fires N parallel POSTs, prints how many finished **&lt; 1 s** vs **≥ 5 s**, then queries `payment.payments`.

**`-Count 10` — you want**

```
codes: 201x10
finished in < 1s: 0
finished in >= 5s: 10
Completed | (none) | 10
```

**`-Count 100` — you want** (shape, not a perfect 10)

```
finished in < 1s: ~90          ← bulkhead (RateLimiterRejectedException)
finished in >= 5s: ~10         ← the Slow slots, Completed
```

| You see | What it is | What you do |
|---|---|---|
| script: API not reachable / `000` | nothing on 5224 | `--launch-profile http`, health 200 |
| all 10 `TimeoutRejectedException` | timeout still **2** | restart with `TimeoutSeconds=30` |
| many HTTP **429** | leftover rate limit | `RateLimit__PerMinute=1000` and restart |
| `column "status" does not exist` | old `psql -c` quoting | `git pull` (script pipes stdin) |

---

## 4. Fix 2 — async (the real product fix)

**What you fix:** the bank is **not** on `POST /orders`. Queue + background processor. Checkout returns in milliseconds even if the gateway is Slow. The order may later `Cancel` — intake still flowed.

Ctrl+C.

```powershell
$env:Payment__Mode = "Async"
$env:Payment__Gateway__Behavior = "Slow"
$env:Payment__TimeoutSeconds = "2"
$env:Payment__MaxConcurrentCharges = "10"
dotnet run --project src/Tadka.Api --launch-profile http
```

```powershell
curl.exe -s -w "`nHTTP %{http_code} time=%{time_total}s`n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
```

**You want:** HTTP **201**, `"Created"`, **~20 ms**. A few seconds later GET may be `Cancelled` (Slow + timeout 2) — **the POST did not wait**.

Shipped is Async + **Fast**: same milliseconds, then GET **Confirmed**.

The in-memory Channel is enough **in one process**. If the process crashes, queued charges are lost. That wound is Day 8 / Kafka. Do not add Kafka today.

---

## 5. Prove the seam (grep)

Textbook modular-monolith and CQRS trees vs this repo (no refactor): [`docs/learn/modular-monolith.md`](../learn/modular-monolith.md).

Day 8 can **move** Payment because Ordering does not reference it.

```powershell
Get-ChildItem src\Tadka.Api\Domain\Orders,src\Tadka.Api\Controllers\OrdersController.cs -Recurse -Filter *.cs |
  Select-String "Modules.Payments|Domain.Payments|PaymentDbContext"
```

**You want:** no output. Hits = you are not on committed `day-07`. Grep **Orders + OrdersController only** (`Domain/Payments/Payment.cs` is allowed — that is the module).

---

## Done when

- [ ] Redis down: menu **200**, SSE **503**, order POST **201**
- [ ] Replica: `GET namaste` after master stop; Cluster: `MOVED`; Sentinel: `.10` then `.11`
- [ ] `payment` schema has `payments` + `__EFMigrationsHistory`
- [ ] Shipped: POST `Created` in tens of ms; GET `Confirmed`
- [ ] Brownout: POST **~8–10 s**
- [ ] Timeout: POST **~3 s**, order `Cancelled`, `TimeoutRejectedException`
- [ ] Burst: `-Count 10` → 10 Completed; `-Count 100` → ~10 Completed + ~90 `RateLimiterRejectedException`
- [ ] Async+Slow: POST **~20 ms** `Created`
- [ ] Grep over Ordering is empty
- [ ] `dotnet test` → **32/32**

## If something looks wrong

| You see | Lesson or setup? | What you do |
|---|---|---|
| env did nothing | setup | two underscores; Ctrl+C; new `dotnet run` |
| curl **000** | setup | API down. `--launch-profile http`, `/health` 200 |
| ConnectionString not initialized | setup | you omitted `--launch-profile http` |
| POST **500** in Synchronous | setup | wrong commit |
| HTTP **201**, ~9 s | **brownout (§2)** | working |
| HTTP **201**, ~3 s, `Cancelled` | **timeout (§3)** | working |
| burst ~10 slow + ~90 fast rejects | **bulkhead (§3b)** | working |
| burst: 10 × `TimeoutRejected` | setup | timeout still 2 → set 30 |
| burst: HTTP **429** | setup | `RateLimit__PerMinute=1000` |
| Sentinel `.11` **before** you stop | leftover toy | `down -v` then `up -d` |
| menu **500** with Redis down | setup | fallback missing |
| full reset | — | `docker compose down -v`, `up -d`, new shell, `dotnet run --launch-profile http` with **no** Payment env |

Slow is fixed **today in one process**. You do **not** extract Payment because it was slow. Day 8’s reason is fault / PCI / data.

Next: Day 8 — `Tadka.Payment.Api` `:5240` + `payment-db` `:5434`.
