# Day 7 — Runbook: payment brownout → timeout + bulkhead → async

**Branch:** `day-07`. **What's new (taught):** payment is wired, and it **fails first**. Polly timeout + bulkhead (ADR-021), MediatR Payment module with its own `PaymentDbContext` / `payment` schema / migration history (ADR-022), async payment off the request path (ADR-023). Infra is Day 6: Postgres `5432`, replica `5433`, Redis `6379`. **No Kafka. No Payment HTTP service** (that is Day 8).

**The number on the board:** Naive → Fix 1 → Fix 2. Fill it as you go. Captured here: **~8.8 s → ~2.9 s → ~20 ms**.

> **Windows PowerShell:** `curl.exe`. Quote `@file`. Env vars use **double underscore** (`Payment__Mode`). Wrong `_` = silent no-op (worse than an error). Each `$env:` lives only in **that** shell — open a fresh one to reset to shipped `appsettings.Development.json`.

| Thing | Value |
|---|---|
| API | `http://localhost:5224` |
| POST body | `@docs/runbooks/place-order.json` (Priya + Meghana biryani) |
| Payment tables | schema `payment` on the **same** Postgres as orders |

### Env levers (read this before any restart)

| Variable | Naive (break) | Fix 1 | Fix 2 / shipped |
|---|---|---|---|
| `Payment__Mode` | `Synchronous` | `Synchronous` | `Async` |
| `Payment__Gateway__Behavior` | `Slow` | `Slow` | `Slow` or `Fast` |
| `Payment__TimeoutSeconds` | `30` (no timeout) | `2` | `2` |
| `Payment__MaxConcurrentCharges` | `1000` (unbounded) | `10` | `10` |

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
dotnet run --project src/Tadka.Api
```

**What it does:** migrates **core** then **Payment** (`PaymentDbContext` history in schema `payment`). Redis still there from Day 6.

**Ready when:** three containers healthy, API **5224**, tests **32/32**.

```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT tablename FROM pg_tables WHERE schemaname='payment' ORDER BY 1;"
```

**Look for:** `payments` and `__EFMigrationsHistory`. Core history stays in `public`.

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

The curl above proved **timeout** (one call, 2 s). It did **not** prove the bulkhead. That needs a **parallel** burst, and a timeout that **outlives** the Slow 8 s delay — otherwise the ten slot-holders fail with `TimeoutRejectedException` and you get **zero** Completed.

HTTP is still **201** for every POST (the order is created first). “Succeed” means `payment.Status = Completed`.

Ctrl+C. **Timeout 30**, keep cap 10, keep Slow:

```powershell
$env:Payment__Mode = "Synchronous"
$env:Payment__Gateway__Behavior = "Slow"
$env:Payment__TimeoutSeconds = "30"
$env:Payment__MaxConcurrentCharges = "10"
$env:RateLimit__PerMinute = "1000"   # leftover weekday limiter; default 120/min 429s a 100-burst
dotnet run --project src/Tadka.Api --launch-profile http
```

```powershell
.\docs\demo-scripts\06-bulkhead-burst.ps1 -Count 10
# expect: 10 Completed, all ~8 s

.\docs\demo-scripts\06-bulkhead-burst.ps1 -Count 100
# expect: ~10 Completed (~8 s) + ~90 RateLimiterRejectedException (milliseconds)
```

`Fast` is a trap: slots free in ~200 ms and more than 10 get through. Laptop stagger means **about 10**, not a perfect 10. Do not mix this with the 2 s timeout demo without a restart.

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

- [ ] `payment` schema has `payments` + `__EFMigrationsHistory`
- [ ] Shipped: POST `Created` in tens of ms; GET `Confirmed`; payment `Completed`
- [ ] Brownout: POST **~8–10 s**
- [ ] Polly: POST **~3 s**; order `Cancelled`; `TimeoutRejectedException`
- [ ] Bulkhead burst: timeout **30** + cap 10; `-Count 10` → 10 Completed; `-Count 100` → ~10 Completed + ~90 `RateLimiterRejectedException`
- [ ] Async+Slow: POST **~20 ms** `Created` (later may Cancel)
- [ ] Grep over Ordering is empty
- [ ] `dotnet test` → **32/32**

## Troubleshooting

| Symptom | What to do |
|---|---|
| Env did nothing | Single `_`. Must be `Payment__Gateway__Behavior`. Fresh shell to reset |
| POST 500 in Synchronous | Need the snapshot-before-publish `PublishEventsAsync` on this branch |
| Payment row missing | `sleep 2` in async; watch logs `Payment COMPLETED` / `FAILED` |
| `column "status" does not exist` | EF `"Status"`. Use the here-string, not `psql -c "SELECT Status"` |
| Reset | `docker compose down -v`, `up -d`, `dotnet run` with **no** Payment env |

**Do not extract Payment because it was slow.** Slow is fixed **today** in one process. Day 8’s reason is fault / PCI / data.

Next: Day 8 — `Tadka.Payment.Api` `:5240` + `payment-db` `:5434`.
