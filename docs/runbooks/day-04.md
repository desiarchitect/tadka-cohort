# Day 4 — Runbook: idempotency, 409, domain events

**Branch:** `day-04`. **What's new:** `Idempotency-Key`, `xmin` → 409, in-process `OrderConfirmed` handler (log line). Saturday's API stays.

> **Class start (instructor):** do **not** open on this branch. Checkout **`day-03`**, wipe, API up. Opening demo: `race-status.ps1` → **two HTTP 204**. Then §0 onto **`day-04` once** and stay. Beat 1 (idempotency) and Beat 2 (409) both run here. Do not switch back to `day-03` mid-class.

### Demo → code (open these when someone asks "yeh kahan hai?")

Spoken cue in the script: **"Ab demo."** Lost-update is **live on `day-03` at the start of the day**, then drawn (t1–t6), then the same script on `day-04`.

| When (script) | What you run | What it proves | Code to point at |
|---|---|---|---|
| **Opening — leftover bug** | On **`day-03`**: POST an order, `.\docs\runbooks\race-status.ps1 -OrderId PASTE_ID` | **Two HTTP 204.** Both writers think they confirmed. No 409. | Day 3 `OrdersController.UpdateStatus` → `SaveChanges` only. No `xmin`. |
| Beat 1 — two POSTs, no key | After checkout **`day-04`**: `POST /api/v1/orders` twice, no header | Missing design: two 201, two ids | `OrdersController.Create` — no key path, just `Add` + `SaveChanges` |
| Beat 1 — same `Idempotency-Key` | two POSTs, header `demo-key-123` | 201 then **200**, same id | `IIdempotencyStore` / `IdempotencyKey` / `ordering.idempotency_keys` (PK = unique). Concurrent loser: unique catch in `Create` → 200 |
| Beat 2 — board | draw t1–t6 Confirm vs Cancel | Same shape as the two 204s: both read `Created`, both write | Legal Cancel-after-Confirm: `OrderStateMachine.cs` 6–8 (not the live race) |
| Beat 2 — **"Ab demo. Fix."** | Same `race-status.ps1` on **`day-04`**, new order, two **Confirmed** | **204+409** (race) or **204+422** (serialised). Not two 204s. | `OrderConfiguration` xmin → middleware 409. Guaranteed 409: `dotnet test --filter xmin_concurrency_token_rejects_a_stale_write` |
| Beat 2 — **"Ab demo. Pessimistic."** | `toydemo/day-04-locking/locking-toy` `hold` / `wait` / `nowait` / `skip` | Default `FOR UPDATE` **waits**; NOWAIT errors; SKIP takes the other row | **`toydemo/day-04-locking/locking-toy/demo.js` only.** Not `OrdersController`. Not `CouponsController`. Table `locking_demo`. |
| Beat 3 — confirm + log | `PATCH … Confirmed` | SMS after commit | `OrderConfirmedEvent` + `OrderConfirmedNotificationHandler` (log line). `DispatchAsync` **after** `SaveChanges` in `UpdateStatus` |

**Do not open for the spine:** `CouponsController` (leftover 50-way), `Demo:DispatchEventsBeforeCommit` (Day 7), `cursor-pagination-toy` (Day 5).

> **Windows PowerShell:** use **`curl.exe`**. From the repo root. Quote `@file` so PowerShell does not splat. Do not paste `-d "{...}"` into PowerShell — it mangles JSON.

| Thing | Value |
|-------|--------|
| API | `http://localhost:5224` |
| Compose service | `postgres` (container `tadka-postgres`) |
| Postgres | `localhost:5432`, db/user `tadka`, password `tadka_local` |

`down -v` when switching onto this branch. Service name is **`postgres`**, not `db`.

Seed GUIDs (same as Day 3): Meghana `a1b2c3d4-0001-4000-8000-000000000001`, biryani `b1b2c3d4-0001-4000-8000-000000000001`, Priya `c1b2c3d4-0001-4000-8000-000000000001`.

## 0. Fresh volume + tests (pre-class)

Instructor: run this **once before class** to confirm 24/24, then **checkout `day-03`** and wipe again so the opening demo is Saturday's API (see **§3a**). After two 204s, come back here onto `day-04` and stay.

Day 2/3 already created `ordering.orders`. If that volume is still there, `dotnet run` tries `CREATE TABLE` again → **`42P07: relation "orders" already exists`**. `down` without **`-v`** is not enough. The container name is always **`tadka-postgres`**, so **another clone** (`D:\work\cohort\tadka`) can keep the old volume alive.

**Stop the API first** (Ctrl+C). Then from **this** repo root (`tadka-cohort` or `tadka`):

```powershell
git checkout day-04

# wipe THIS project's compose volume
docker compose down -v

# also wipe the shared container + both usual volume names
docker rm -f tadka-postgres
docker volume rm tadka_pgdata tadka-cohort_pgdata
# compose `name: tadka` means the live volume is usually tadka_pgdata even from tadka-cohort

docker compose up -d
docker compose ps              # tadka-postgres (healthy)

dotnet build Tadka.slnx
dotnet test Tadka.slnx
dotnet run --project src/Tadka.Api
```

If `volume rm` says "no such volume", ignore it. If it says "volume is in use", `docker rm -f tadka-postgres` again, then `volume rm`.

**Look for:** tests **24/24**. Migrations `InitialDomainModel`, `OrderLifecycleAndDemoSeed`, `Day04Hardening` (and `Day04CouponLocking` — not taught live). Listen **5224**. Docker Desktop must be running (Testcontainers).

## 1. Body file

[`place-order.json`](place-order.json) — Priya, Meghana, 2× Chicken Biryani, no price. Quote the `@` path.

## 2. Beat 1 — double tap (live)

Two POSTs, **no** key → **two different** order ids.

```powershell
curl.exe -s -w "`nHTTP %{http_code}`n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
curl.exe -s -w "`nHTTP %{http_code}`n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
```

Same `Idempotency-Key` twice → first **201**, second **200**, **same** id.

```powershell
curl.exe -s -w "`nHTTP %{http_code}`n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -H "Idempotency-Key: demo-key-123" --data-binary "@docs/runbooks/place-order.json"
curl.exe -s -w "`nHTTP %{http_code}`n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -H "Idempotency-Key: demo-key-123" --data-binary "@docs/runbooks/place-order.json"
```

Do **not** demo same key + different body. The API still returns the first order (ADR-011 revisit: Stripe would 422).

## 3. Beat 2 — optimistic (Tadka) then pessimistic (toy)

### 3a. Class start — show the bug on **`day-03`** (before you move to Day 4)

Day 4 already has `xmin`. **First thing in class** (and first thing if you self-study): stay on **`day-03`** so both writers can get **204**. Then checkout `day-04` once. Do not leave this until after Beat 1.

**Ctrl+C** the API if it is already on day-04.

```powershell
git checkout day-03
docker compose down -v
docker rm -f tadka-postgres
docker volume rm tadka_pgdata tadka-cohort_pgdata
docker compose up -d
docker compose ps
dotnet run --project src/Tadka.Api
```

New terminal, repo root. New order, copy `"id"`:

```powershell
curl.exe -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
.\docs\runbooks\race-status.ps1 -OrderId PASTE_ID
```

**Look for two HTTP 204.** Both callers think they confirmed. No 409. `GET /api/v1/orders/PASTE_ID` is still `Confirmed`.

Do **not** use Confirm then Cancel: that path is legal (`OrderStateMachine.cs` 6–8). Two 204s there is a normal cancel.

Then draw t1–t6 (Confirm vs Cancel, last write wins, restaurant cooking, no error). Same shape: both read `Created`, both write.

**Day 3 code (no token):** `OrdersController.UpdateStatus` → `SaveChanges` only. No `xmin` in configuration.

### 3b. After the opening — `day-04` and prove the fix

If class started on `day-03`, you already wiped onto this branch before Beat 1. **Ctrl+C** only if the API is still on Day 3. Wipe again (Day 4 migrations) if you just checked out.

```powershell
git checkout day-04
docker compose down -v
docker rm -f tadka-postgres
docker volume rm tadka_pgdata tadka-cohort_pgdata
docker compose up -d
dotnet run --project src/Tadka.Api
```

Same race, **new** order, **two Confirmed** (not Confirm+Cancel):

`Created → Confirmed → Cancelled` is **legal**. If you run Confirm, wait, then Cancel, you get **two 204s**. That is a normal cancel, not a race. `Start-Job` is too slow: the first PATCH finishes before the second starts.

`Confirmed → Confirmed` is **illegal**. So two Confirmed cannot both 204:

| Codes | Meaning |
|---|---|
| **204** and **409** | Both read `Created`. Second `SaveChanges` saw a new `xmin`. Race caught. |
| **204** and **422** | Second ran after commit. State machine rejected Confirmed→Confirmed. Still no silent overwrite. |
| **Two 204s** | You used Confirm+Cancel, or an old order. New 201, run the script again. |

**Where the fix lives**

| Step | File | Lines |
|---|---|---|
| Map Postgres `xmin` as concurrency token | `src/Tadka.Api/Data/Configurations/OrderConfiguration.cs` | 45–54 (`IsConcurrencyToken()`) |
| PATCH reads order, transitions, `SaveChanges` | `src/Tadka.Api/Controllers/OrdersController.cs` | 150–191 (comment + `SaveChanges` 185–187) |
| `DbUpdateConcurrencyException` → **409** | `src/Tadka.Api/Middleware/ExceptionHandlingMiddleware.cs` | 50–58 |
| Illegal jump → **422** | `OrdersController.cs` 162–164; `OrderStateMachine.cs` 6–8 | |
| Deterministic 409 (two DbContexts) | `tests/.../OrderFlowIntegrationTests.cs` | `xmin_concurrency_token_rejects_a_stale_write` ~116 |

Orders do **not** use `FOR UPDATE`. That is the toy in 3c.

**Run** (API up, repo root):

```powershell
curl.exe -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
.\docs\runbooks\race-status.ps1 -OrderId PASTE_ID
```

Guaranteed **409** (no HTTP luck):

```powershell
dotnet test Tadka.slnx --filter xmin_concurrency_token_rejects_a_stale_write --nologo
```

### 3c. Pessimistic primitives (toy, not orders)

Toy, own table, two terminals. Postgres must be up. Full doc: [`toydemo/day-04-locking/locking-toy/RUN-AND-TEST.md`](../../toydemo/day-04-locking/locking-toy/RUN-AND-TEST.md).

```powershell
cd toydemo\day-04-locking\locking-toy
# window A
node demo.js hold
# window B immediately
node demo.js wait      # ~5s block, not an error
node demo.js nowait    # could not obtain lock
node demo.js skip      # id=2
```

Do **not** run the cursor-pagination toy today. That is Day 5.

## 4. Beat 3 — SMS after commit (live)

`PATCH …/status` `Confirmed`. Watch the **`dotnet run` log**, not only HTTP:

```
Notification: order … confirmed — SMS sent to customer …
```

Handler ran after commit. A failed SMS must not un-confirm.

```powershell
curl.exe -s -w "`nHTTP %{http_code}`n" -X PATCH http://localhost:5224/api/v1/orders/$ORDER/status -H "Content-Type: application/json" --data-binary "@docs/runbooks/status-confirmed.json"
```

## Done when

- [ ] 24/24 tests
- [ ] On **`day-03`**: two Confirmed → **two 204s** (the leftover bug)
- [ ] Then **`day-04`** once: two ids without a key; 201 then 200 with a key
- [ ] Same `race-status.ps1` on day-04 → **204+409** or **204+422** (not two 204s)
- [ ] You can explain 409 vs 422
- [ ] Toy `wait` freezes ~5s
- [ ] Confirm prints the notification line
- [ ] You can name the file for each demo (table at the top of this runbook)

## Troubleshooting

| Symptom | What to do |
|---------|------------|
| `42P07: relation "orders" already exists` | Old Day 2/3 volume. Ctrl+C the API, then the wipe block in **§0** (`down -v` **and** `docker rm -f tadka-postgres` + both `pgdata` volumes). Then `up -d` and `dotnet run`. |
| Two POSTs with a key still create two orders | Header name is `Idempotency-Key`. Same key both times. |
| `dotnet test` hangs / Docker errors | Start Docker Desktop; Testcontainers needs it. |
| No notification in the log | Confirm a **Created** order; look at the API process terminal. |
| POST/PATCH 400, `invalid start of a property name` | PowerShell stripped the quotes in `'{"status":"Confirmed"}'`. Use a file: `"@docs/runbooks/status-confirmed.json"`. Never inline JSON on PowerShell. |
| Coupons / `FOR UPDATE` in the repo | Not today's spine. Ignore `CouponsController` unless leftover time. |
| Container name already in use | Another folder started `tadka-postgres`. `docker rm -f tadka-postgres`, then §0. |

Next: Day 5 — indexes, pool, replica. Not Sunday.
