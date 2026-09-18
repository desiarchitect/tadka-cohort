# Day 7 — Runbook: payment brownout → timeout + bulkhead → async

**Branch:** `day-07`. **What changed since Day 6:** [`docs/changelog.md`](../changelog.md). **What's new (taught):** payment is wired, and it **fails first**. Polly timeout + bulkhead (ADR-021), MediatR Payment module with its own `PaymentDbContext` / `payment` schema / migration history (ADR-022), async payment off the request path (ADR-023). Infra is Day 6: Postgres `5432`, replica `5433`, Redis `6379`. **No Kafka. No Payment HTTP service** (that is Day 8).

**The number on the board:** Naive → Fix 1 → Fix 2. Fill it as you go. Captured here: **~8.8 s → ~2.9 s → ~20 ms**.

> **Windows PowerShell:** `curl.exe` (not `curl` — that alias is `Invoke-WebRequest`). Quote `@file`. Env vars use **double underscore** (`Payment__Mode`). Wrong `_` = silent no-op (worse than an error). Each `$env:` lives only in **that** shell — open a fresh one to reset to shipped `appsettings.Development.json`. `dotnet run` already running **does not** reread env. Prefer `--launch-profile http` so you get port **5224** and `ASPNETCORE_ENVIRONMENT=Development` (connection string). Without a profile the ConnectionString can be empty.

**How to read a POST curl in this file**

| Flag | What it does |
|---|---|
| `curl.exe` | The real curl binary |
| `-s` | quiet (no progress bar) |
| `-o NUL` | throw the body away (timing-only runs) |
| `-w "…%{http_code} %{time_total}s"` | print status and seconds after the call |
| `-X POST` | create an order |
| `-H "Content-Type: application/json"` | JSON body |
| `--data-binary "@docs/runbooks/place-order.json"` | read the file as-is (`@` = file, not the letters `@docs`) |
| `--max-time 20` | give up if the brownout hangs longer than 20 s |

`000` from curl = **nothing listened** (API down). That is a **setup fail**, not the lesson.

| Thing | Value |
|---|---|
| API | `http://localhost:5224` |
| POST body | `@docs/runbooks/place-order.json` (Priya + Meghana biryani) |
| Payment tables | schema `payment` on the **same** Postgres as orders |

### Env levers (read this before any restart)

| Variable | Naive (break) | Fix 1 (timeout) | Burst §3b (bulkhead) | Fix 2 / shipped |
|---|---|---|---|---|
| `Payment__Mode` | `Synchronous` | `Synchronous` | `Synchronous` | `Async` |
| `Payment__Gateway__Behavior` | `Slow` | `Slow` | `Slow` (holds a slot ~8 s) | `Slow` or `Fast` |
| `Payment__TimeoutSeconds` | `30` (no timeout) | **`2`** (prove timeout) | **`30`** (must outlive 8 s or the 10 fail) | `2` |
| `Payment__MaxConcurrentCharges` | `1000` (unbounded) | `10` | **`10`** | `10` |
| `RateLimit__PerMinute` | (default 120) | (default 120) | **`1000`** (else a 100-burst 429s) | (default 120) |

Shipped file is **Async + Fast + 2s + 10**. Ctrl+C the API, set `$env:…`, `dotnet run` again. `dotnet run` already running **does not** reread env.

### Demo → code

| When | What you run | Look for | Code |
|---|---|---|---|
| Shipped | POST, wait, GET | 201 `Created` in tens of ms; GET `Confirmed`; payment `Completed` | `PaymentProcessor` hosted service |
| Two histories | `pg_tables` schema `payment` | `payments` **and** `__EFMigrationsHistory` | `Program.cs` 106, 126 |
| Brownout | Sync + Slow + timeout 30 | POST **~8.8 s**, still 201 | gateway sleeps 8 s on the request path |
| Fix 1 | Sync + Slow + timeout 2 | POST **~2.9 s**; order `Cancelled`; `TimeoutRejectedException` | `PaymentResiliencePipeline.cs` 28–33 |
| Bulkhead burst | Sync + Slow + timeout **30** + cap 10 | 10 parallel → 10 `Completed`; 100 parallel → ~10 `Completed` + ~90 `RateLimiterRejected` | same pipeline, `queueLimit: 0` |
| Fix 2 | Async + Slow | POST **~20 ms**, status `Created`; later `Cancelled` | `PaymentWorkChannel` + processor |
| Grep | Select-String on Orders | **no matches** | ADR-022 seam |

Spoken cue: **"Ab demo."**

---

## How payment is wired (.NET, and the same idea elsewhere)

```
POST /orders
    → SaveChanges (order Created)
    → MediatR Publish OrderPlaced
         │
         ├─ Synchronous mode: charge NOW (Polly around FakePaymentGateway)
         └─ Async mode: enqueue; PaymentProcessor charges in the background
    → HTTP returns  (async: milliseconds; sync: waits for the gateway)
```

| Job | Tadka (.NET) | Java | Node |
|---|---|---|---|
| Timeout + bulkhead | **Polly** v8 `AddTimeout` + `AddConcurrencyLimiter` | Resilience4j | Opossum / p-limit |
| In-process events | **MediatR** `INotification` | Spring `ApplicationEventPublisher` | EventEmitter |
| Off the request path | `Channel` + `BackgroundService` | `@Async` / queue | worker thread / Bull |

Polly is not the lesson. **Bound how long one call holds a slot, and how many slots exist.**

---

## 0. Fresh start

```powershell
git checkout day-07
docker compose down -v
docker rm -f tadka-postgres tadka-postgres-replica tadka-redis
docker compose up -d
dotnet test Tadka.slnx          # 32 cases on this merge. Needs Docker.
# No Payment__* in this shell.
dotnet run --project src/Tadka.Api --launch-profile http
```

**What it does:** `down -v` wipes volumes (clean DB). `up -d` starts Postgres `5432`, replica `5433`, Redis `6379`. `dotnet run --launch-profile http` migrates **core** then **Payment** and listens on **5224**. Redis still there from Day 6.

**Ready when:** three containers healthy, `curl.exe http://localhost:5224/health` → **200**, tests **32/32**. Do not start §3b until health is 200.

```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT tablename FROM pg_tables WHERE schemaname='payment' ORDER BY 1;"
```

**Look for:** `payments` and `__EFMigrationsHistory`. Core history stays in `public`.

---

## 0b. Redis in production (Day 6 leftover — 18 min)

Day 6 taught **commands**. This beat is **what production does**: Redis down, replica, Cluster, Sentinel HA, and “same seat from another country.”

Student one-pager: [`docs/learn/redis-in-production.md`](../learn/redis-in-production.md). Toy (not Tadka compose): [`toydemo/day-07-redis-ha/`](../../toydemo/day-07-redis-ha/).

Tadka stays **standalone** `tadka-redis` :6379. We do **not** point the API at Cluster or Sentinel today.

Pre-class (so S0 does not wait on image pulls):

```powershell
docker compose -f toydemo/day-07-redis-ha/docker-compose.yml up -d
```

API for beat 1 must already be running (`--launch-profile http`, health **200**). `$RID` = `a1b2c3d4-0001-4000-8000-000000000001` (Meghana).

### Beat 1 — kill Tadka Redis (classification)

**Story:** Redis is **down**. Menu must still work. SSE must fail honest. Orders do not use Redis. `docker compose start redis` at the end is a **reset**, not HA.

**Not the lesson:** `000` = API down. Menu **500** = no fallback (wrong commit).

```powershell
# DOING: stop Tadka's Redis only (not the HA toy).
# PROVES: the API process stays up.
docker compose stop redis

# DOING: GET menu with cache gone (miss -> SQL).
# PROVES: cache is a PERFORMANCE dep. Expect HTTP 200.
# NOT: 000 (API down) or 500 (no fallback). First hit can be 5-12s — do not Ctrl+C.
curl.exe -s -o NUL -w "menu %{http_code} %{time_total}s`n" http://localhost:5224/api/v1/restaurants/$RID/menu

# DOING: open the live-tracking SSE stream.
# PROVES: SSE is a CORRECTNESS dep for the stream. Expect HTTP 503 + "Live tracking requires Redis".
curl.exe -s -o NUL -w "sse  %{http_code}`n" --max-time 5 http://localhost:5224/api/v1/orders/00000000-0000-0000-0000-000000000001/events

# DOING: place an order (Postgres, not Redis).
# PROVES: money path does not need Redis. Expect HTTP 201.
curl.exe -s -o NUL -w "order %{http_code}`n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"

# DOING: bring Tadka Redis back. RESET for the rest of class. NOT "we added HA".
docker compose start redis
docker exec tadka-redis redis-cli PING
# expect: PONG
```

### Beat 2 — replica (`REPLICAOF`) is a copy, not HA

**Story:** a replica is a **copy**. Killing the master does **not** fail over. Data on the replica survives; an app still aimed at the master is **dead**. `docker start` is **reset**, not the fix (that is Beat 4 Sentinel).

**How it is set up:** `redis-server --replicaof 172.28.0.10 6379` (toy replica on host **6381**).

```powershell
# DOING: write on the ONLY writer (master).
docker exec tadka-ha-master redis-cli SET demo:ha namaste

# DOING: read the same key from the replica.
# PROVES: replication works. Expect: namaste. NOT: failover / HA.
docker exec tadka-ha-replica redis-cli GET demo:ha

# DOING: ask the replica who it is.
# PROVES: it is a follower. Expect: role:slave
docker exec tadka-ha-replica redis-cli INFO replication

# DOING: FAIL THE WRITER. This is the failure we are studying.
docker stop tadka-ha-master

# DOING: read the replica after the master is dead.
# PROVES: the COPY survived. Expect: namaste.
# THE FAIL: any app still pointing at the master (host 6380) is down. Copy != failover.
docker exec tadka-ha-replica redis-cli GET demo:ha

# DOING: bring the writer back. RESET the lab. NOT Sentinel. NOT the product fix.
docker start tadka-ha-master
```

**Demo fail:** GET nil → replica not ready, wait 2s. **Setup fail:** container missing → toy compose not up.

**Line:** “Data lived. The app that still points at the master is dead.”

### Beat 3 — Cluster is 16384 slots (`MOVED`), not HA

**Story:** keys are **sharded**. `MOVED` is Cluster working, not a bug. Killing a node **without replicas** loses those slots. `docker start c2` is reset, not HA (`--cluster-replicas 1` would be HA).

**How it is set up:** `cluster-enabled yes` + `cluster-announce-ip` as a **literal IP**. Then `--cluster create … --cluster-replicas 0`.

```powershell
# DOING: list the three masters and their slot ranges.
# PROVES: cluster formed. Expect: three master lines, IPs 172.28.0.21-23.
docker exec tadka-redis-c1 redis-cli CLUSTER NODES

# DOING: write WITHOUT following redirects.
# PROVES: this key lives on another node. Expect: (error) MOVED <slot> 172.28.0.2x:6379
# NOT: a bug. THAT is Cluster (sharding).
docker exec tadka-redis-c1 redis-cli SET user:1 a

# DOING: same write with -c (client follows MOVED). Then read.
# PROVES: a cluster-aware client can write/read. Expect: OK then a.
# Use docker exec (MOVED is a Docker IP — host redis-cli cannot follow).
docker exec tadka-redis-c1 redis-cli -c SET user:1 a
docker exec tadka-redis-c1 redis-cli -c GET user:1

# DOING: FAIL ONE SHARD (not the replica from Beat 2).
docker stop tadka-redis-c2

# DOING: read after that shard is dead.
# PROVES: those slots had 0 replicas. Expect: CLUSTERDOWN or error. THAT is the fail.
# NOT: the replica beat (copy survived). Here the KEYSPACE is gone.
docker exec tadka-redis-c1 redis-cli -c GET user:1

# DOING: bring the shard back. RESET. NOT --cluster-replicas 1.
docker start tadka-redis-c2
```

**Setup fail:** empty NODES before you killed a node → `docker start tadka-redis-c-init`.

**Line:** sharding ≠ HA.

### Beat 4 — Sentinel is how failover is set up (the actual fix)

**Story:** same `docker stop` as Beat 2, but now a voter **changes the writer**. That is HA. Tadka would still not follow (hardcoded `:6379`). `docker start` at the end is reset.

**How it is set up:** `sentinel monitor mymaster 172.28.0.10 6379 1` (static IP). Client talks to Sentinel (`:26379`), not a hardcoded master. ElastiCache primary endpoint **is** this.

```powershell
# DOING: ask Sentinel who the writer is, before the fail.
# PROVES: Sentinel is watching. Expect: 172.28.0.10 6379 (the master).
docker exec tadka-ha-sentinel redis-cli -p 26379 SENTINEL get-master-addr-by-name mymaster

# DOING: the SAME fail as Beat 2 (kill the writer).
docker stop tadka-ha-master
Start-Sleep -Seconds 8

# DOING: ask Sentinel again.
# PROVES: THE FIX — writer address CHANGED. Expect: 172.28.0.11 6379 (the old replica).
# NOT: "GET still namaste" (that was Beat 2, the copy). Here the *role* moved.
docker exec tadka-ha-sentinel redis-cli -p 26379 SENTINEL get-master-addr-by-name mymaster

# DOING: confirm on the replica process.
# PROVES: it was promoted. Expect: role:master
docker exec tadka-ha-replica redis-cli INFO replication

# DOING: RESET. Old master may come back as a replica. NOT required for the proof.
docker start tadka-ha-master
```

| After `stop` master | Beat 2 replica | Beat 4 Sentinel |
|---|---|---|
| Data | Copy still `GET`s | Copy still `GET`s |
| Writer | Still the dead host | **Address changes** to the replica |
| App with hardcoded host | **Dead** | Still dead unless it talks to **Sentinel** |
| `docker start` | Reset | Reset |

If failover does not happen in 10s: show `sentinel.conf`, say the line, move on. Do not steal the brownout.

Tadka still has `localhost:6379` in config — it would **not** follow Sentinel. That is honest.

### Beat 5 — same seat, two countries (talk, 3 min)

**Not Cluster.** Cluster is one region. Mumbai Redis and London Redis are two caches. Async replica: both `GET seat:12A` free → double booking.

**Fix:** one inventory writer (unique `(flight, seat)`, `UPDATE … WHERE free RETURNING`, hold TTL). Regional Redis may cache the **map**. The book click always hits the seat service. US→India RTT is the cost of one seat. Last-writer-wins / CRDT cannot be a seat.

Tadka: cache the menu. Never cache order status.

---

## 1. BASELINE — shipped async + fast (ADR-023)

**Story:** Swiggy shows “order placed” immediately. The bank is not on that HTTP call.

```powershell
curl.exe -s -w "`nHTTP %{http_code} time=%{time_total}s`n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
```

Copy `"id"` and `"status"`. First hit after boot can be ~0.8 s (JIT). **Second** POST is the number:

**Captured:** HTTP **201**, `status":"Created"`, warm **time=0.022s**.

Wait 2 s, then:

```powershell
curl.exe -s http://localhost:5224/api/v1/orders/PASTE_ID
```

**Look for:** `"status":"Confirmed"`.

Payment row (quoted `"Status"` — Windows `psql -c` strips quotes, use a here-string):

```powershell
@'
SELECT "Status", "FailureReason", "CompletedAt" FROM payment.payments ORDER BY "CreatedAt" DESC LIMIT 1;
'@ | docker exec -i tadka-postgres psql -U tadka -d tadka
```

**Look for:** `Completed`, empty `FailureReason`.

Optional: `curl.exe -N http://localhost:5224/api/v1/orders/PASTE_ID/events` **before** POST — you should see `Created` then `Confirmed`.

---

## 2. BREAK — brownout (sync + slow, no timeout)

**Story:** Naive design charges **inside** `POST /orders`. Gateway has an incident (8 s). No Polly timeout. Every checkout holds a thread and a DB connection for ~9 s. 50 pool slots ÷ 9 s ≈ **5.5 orders/sec** vs dinner-rush ~11/sec. Half capacity, no bug in your SQL.

Ctrl+C the API. **Same window:**

```powershell
$env:Payment__Mode = "Synchronous"
$env:Payment__Gateway__Behavior = "Slow"
$env:Payment__Gateway__SlowDelaySeconds = "8"
$env:Payment__TimeoutSeconds = "30"
$env:Payment__MaxConcurrentCharges = "1000"
dotnet run --project src/Tadka.Api
```

Other terminal:

```powershell
curl.exe -s -o NUL -w "BROWN HTTP %{http_code} time=%{time_total}s`n" --max-time 20 -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
```

**Identified if:** HTTP **201**, **time ≈ 8.8 s** (8 s sleep + overhead). Teaching capture was **9.2 s**. Your box will differ; **~8–10 s** is the brownout. **500** = not this commit (sync event snapshot).

---

## 3. FIX 1 — Polly 2 s timeout + bulkhead 10 (ADR-021)

Still synchronous (worst case). Bound **duration** (timeout) and **fan-out** (bulkhead). Fail-fast: order **cancels** instead of the app hanging.

Ctrl+C. Fresh values (overwrite the previous `$env:`):

```powershell
$env:Payment__Mode = "Synchronous"
$env:Payment__Gateway__Behavior = "Slow"
$env:Payment__TimeoutSeconds = "2"
$env:Payment__MaxConcurrentCharges = "10"
dotnet run --project src/Tadka.Api
```

Same POST curl as §2.

**Captured:** HTTP **201**, **time=2.86 s**, JSON `"status":"Cancelled"`, `TimeoutRejectedException` … `'00:00:02'`.

Payment row: `"Status"=Failed`, same exception in `"FailureReason"`.

**Is this better?** Ask the room. The user used to succeed slowly; now they get cancelled quickly. That is a real trade. Timeout ≠ bulkhead: timeout is **one** call; bulkhead is **how many** such calls at once.

**Honesty:** capture was **~2.9 s** not 2.1 s. Warm vs cold; do not fake 2.1.

---

## 3b. FIX 1b — bulkhead burst: 10 succeed, 100 → ~10 (ADR-021)

### What we are demoing vs what is a fail

| | This **is** the lesson | This is **not** the lesson (setup fail) |
|---|---|---|
| Wanted | 10 parallel → 10 payments **Completed** (~8 s). 100 parallel → **~10** Completed + **~90** rejected in **ms** | |
| Wanted reject | `RateLimiterRejectedException` on the **payment** row (Polly bulkhead, `queueLimit: 0`) | HTTP `000` (API down), HTTP `429` (leftover `RateLimit:PerMinute=120`), `TimeoutRejectedException` (you left timeout at **2**) |
| HTTP | Still **201** for (almost) every POST. The **order is created first**. “Succeed” = `payment.Status = Completed`, not a 201 vs 429 split | |

§3’s **single** curl proved **timeout** (one Slow call cut at 2 s → order Cancelled). It did **not** prove how many Slow calls may run at once. That is this beat.

### Why we restart (timeout 2 → 30)

Slow gateway sleeps **8 s**. Polly timeout **2** (Fix 1) kills those ten slot-holders → **zero** Completed. Burst needs timeout **30** so the ten that got a slot **finish**. Cap stays **10**. Still Synchronous + Slow.

| Env | Value | Why |
|---|---|---|
| `Payment__Mode` | `Synchronous` | Charge is **on** `POST /orders`, so you feel the 8 s vs the ms reject |
| `Payment__Gateway__Behavior` | `Slow` | Holds a bulkhead slot ~8 s. **`Fast` is a trap** (~200 ms recycle → more than 10 Completed) |
| `Payment__TimeoutSeconds` | **`30`** | Must outlive 8 s |
| `Payment__MaxConcurrentCharges` | **`10`** | The bulkhead. 11th concurrent charge is rejected **now** |
| `RateLimit__PerMinute` | **`1000`** | Weekday leftover limiter. Default **120** will 429 a 100-burst and look like the bulkhead |

Ctrl+C the API (running process **ignores** new `$env:`).

```powershell
$env:Payment__Mode = "Synchronous"
$env:Payment__Gateway__Behavior = "Slow"
$env:Payment__TimeoutSeconds = "30"
$env:Payment__MaxConcurrentCharges = "10"
$env:RateLimit__PerMinute = "1000"
dotnet run --project src/Tadka.Api --launch-profile http
```

`--launch-profile http` = listen **5224** + Development connection string. Wait until:

```powershell
curl.exe -sS -o NUL -w "%{http_code}`n" http://localhost:5224/health
# must print 200. 000 = API not up yet (or wrong port).
```

Other terminal, **repo root**, `day-07`:

```powershell
.\docs\demo-scripts\06-bulkhead-burst.ps1 -Count 10
.\docs\demo-scripts\06-bulkhead-burst.ps1 -Count 100
```

The script: preflights `/health` (throws on `000`); fires N parallel `POST /orders` with `place-order.json` (no `Idempotency-Key`, so N real orders); prints HTTP codes + how many finished **&lt; 1 s** vs **≥ 5 s**; pipes SQL to `psql` on **stdin** (Windows `docker exec -c` strips `"Status"` quotes).

### How to read the script output

**Wave A (`-Count 10`) — demo win**

```
codes: 201x10
finished in < 1s (bulkhead reject): 0
finished in >= 5s (held a Slow slot): 10
Status Completed, reason (none), n = 10   (or ~10; last-45s window may include a prior wave)
```

**Wave B (`-Count 100`) — demo win** (captured on a laptop: `201x97`, `90` in &lt; 1 s, `10` in ≥ 5 s, `87` `RateLimiterRejectedException`)

```
finished in < 1s (bulkhead reject): ~90     <- THE bulkhead
finished in >= 5s (held a Slow slot): ~10
Failed | RateLimiterRejectedException | ~90
Completed | (none) | ~10 in this wave
```

Laptop stagger: **about 10**, not a perfect 10. Shape is the lesson.

**Setup fails (stop, fix, re-run — this is not the bulkhead)**

| You see | What it actually is | Fix |
|---|---|---|
| Script throws: API not reachable / `http_code=000` | Nothing on **5224** | `dotnet run --launch-profile http`, wait for health **200** |
| `000x10` and times ~2 s | Same, older script without preflight | `git pull` then health 200 |
| `column "status" does not exist` | `psql -c` ate the quotes | Current script pipes stdin; `git pull` |
| All 10 `TimeoutRejectedException` | Timeout still **2** | Restart with `TimeoutSeconds=30` |
| More than ~15 Completed on the 100-burst | Gateway **Fast**, or timeout so short slots recycle | Must be **Slow** + 30 |
| Many HTTP **429** | `RateLimit:PerMinute` default 120 | Set `RateLimit__PerMinute=1000` and restart |
| `git pull` aborted (local script dirty) | Uncommitted copy of the `.ps1` | `git restore --worktree -- docs/demo-scripts/06-bulkhead-burst.ps1` then `git pull` |

Could (not Sunday): same 100 in **Async** mode — every POST returns in ms; the bulkhead is on the worker.

---

## 4. FIX 2 — async: intake never waits (ADR-023)

The real fix: **do not put the bank on `POST /orders`.** Queue + background processor. Even a slow gateway returns in milliseconds. Order may later `Cancel` — **checkout still flows**.

Ctrl+C.

```powershell
$env:Payment__Mode = "Async"
$env:Payment__Gateway__Behavior = "Slow"
$env:Payment__TimeoutSeconds = "2"
$env:Payment__MaxConcurrentCharges = "10"
dotnet run --project src/Tadka.Api
```

```powershell
curl.exe -s -w "`nHTTP %{http_code} time=%{time_total}s`n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
```

**Captured:** HTTP **201**, `"status":"Created"`, warm **time=0.021 s**. Four seconds later GET is `Cancelled` (gateway still slow + 2 s timeout) — **intake did not wait**.

Shipped config is Async + **Fast**: same milliseconds, then GET **Confirmed**.

In-memory channel is enough **inside one process**. Crash-loses-the-queue is **Day 8’s wound** / Week 5 Kafka. Do not add Kafka today.

---

## 5. The seam — grep (ADR-022)

**What a modular monolith and CQRS look like** (textbook vs this repo — no refactor): [`docs/learn/modular-monolith.md`](../learn/modular-monolith.md).

Day 8 is a **move**, not a rewrite, because Ordering does not reference Payment.

```powershell
Get-ChildItem src\Tadka.Api\Domain\Orders,src\Tadka.Api\Controllers\OrdersController.cs -Recurse -Filter *.cs |
  Select-String "Modules.Payments|Domain.Payments|PaymentDbContext"
```

**Look for:** no output. If you get hits, you are not on committed `day-07`.

`Domain/Payments/Payment.cs` still exists (the module). Grep **Orders + OrdersController only**.

MediatR replaced the Day-4 hand-rolled dispatcher (`Program.cs` 46). `BoundaryTests.cs` that **fails the build** on a stray import ships on **day-08**, not today.

---

## Done when

- [ ] Redis down: menu **200**, SSE **503**, order POST **201**
- [ ] Replica GET still `namaste` after master stop; Cluster shows `MOVED`; Sentinel names a new master (or you taught the conf)
- [ ] Same seat / two countries: one inventory writer, not two Redis
- [ ] `payment` schema has `payments` + `__EFMigrationsHistory`
- [ ] Shipped: POST `Created` in tens of ms; GET `Confirmed`; payment `Completed`
- [ ] Brownout: POST **~8–10 s**
- [ ] Polly: POST **~3 s**; order `Cancelled`; `TimeoutRejectedException`
- [ ] Bulkhead burst: timeout **30** + cap 10; `-Count 10` → 10 Completed; `-Count 100` → ~10 Completed + ~90 `RateLimiterRejectedException`
- [ ] Async+Slow: POST **~20 ms** `Created` (later may Cancel)
- [ ] Grep over Ordering is empty
- [ ] `dotnet test` → **32/32**

## Troubleshooting

| Symptom | Demo fail or setup? | What to do |
|---|---|---|
| Env did nothing | Setup | Single `_`. Must be `Payment__Gateway__Behavior`. Fresh shell; running `dotnet run` does not reread env |
| `curl` http_code **000** / script “API is not reachable” | Setup | API down or wrong port. `--launch-profile http`, wait for `/health` **200** |
| ConnectionString not initialized | Setup | You ran without the `http` profile / not Development |
| POST **500** in Synchronous | Setup | Need the snapshot-before-publish `PublishEventsAsync` on this branch |
| HTTP **201**, ~9 s | **Demo (brownout)** | Sync + Slow + timeout 30. That is §2 working |
| HTTP **201**, ~3 s, order `Cancelled`, `TimeoutRejectedException` | **Demo (Fix 1)** | Timeout 2 doing its job |
| Burst: ~10 × ≥5 s + ~90 × &lt;1 s, `RateLimiterRejectedException` | **Demo (bulkhead)** | Cap 10 doing its job |
| Burst: 10 × `TimeoutRejectedException`, 0 Completed | Setup | Timeout still 2. Restart at 30 |
| Burst: many HTTP **429** | Setup | `RateLimit__PerMinute=1000` and restart |
| Payment row missing | Setup | `sleep 2` in async; logs `Payment COMPLETED` / `FAILED` |
| `column "status" does not exist` | Setup | EF column is `"Status"`. Pipe SQL on stdin, do not `psql -c "SELECT Status"` |
| `git pull` would overwrite `06-bulkhead-burst.ps1` | Setup | `git restore --worktree -- docs/demo-scripts/06-bulkhead-burst.ps1` then pull |
| Redis-down menu **500** | Setup | Fallback missing — not this `day-07` |
| Replica GET nil | Setup | Wait 2s; toy compose up |
| CLUSTER empty / CLUSTERDOWN before you killed a node | Setup | `docker start tadka-redis-c-init` |
| Sentinel still names old master after 10s | Setup / skip | Teach `sentinel.conf`; do not steal the brownout |
| Reset | — | `docker compose down -v`, `up -d`, fresh shell, `dotnet run --launch-profile http` with **no** Payment env (shipped async/fast) |

**Do not extract Payment because it was slow.** Slow is fixed **today** in one process. Day 8’s reason is fault / PCI / data.

Next: Day 8 — `Tadka.Payment.Api` `:5240` + `payment-db` `:5434`.
