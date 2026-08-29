# Day 4 — Runbook: idempotency, 409, domain events

**Branch:** `day-04`. **What's new:** `Idempotency-Key`, `xmin` → 409, in-process `OrderConfirmed` handler (log line). Saturday's API stays.

### Demo → code (open these when someone asks "yeh kahan hai?")

The **break** for lost-update is **drawn** (t1–t6). Everything else is a live demo. Spoken cue in the script: **"Ab demo."**

| When (script) | What you run | What it proves | Code to point at |
|---|---|---|---|
| Beat 1 — two POSTs, no key | `POST /api/v1/orders` twice, no header | Missing design: two 201, two ids | `OrdersController.Create` — no key path, just `Add` + `SaveChanges` |
| Beat 1 — same `Idempotency-Key` | two POSTs, header `demo-key-123` | 201 then **200**, same id | `IIdempotencyStore` / `IdempotencyKey` / `ordering.idempotency_keys` (PK = unique). Concurrent loser: unique catch in `Create` → 200 |
| Beat 2 — **board** | draw t1–t6 Confirm vs Cancel | Silent last-write-wins | *Not in this branch.* `xmin` is already on. Do not expect two 204s. |
| Beat 2 — **"Ab demo. Fix."** | `.\docs\runbooks\race-status.ps1 -OrderId <new 201 id>` — two **Confirmed**, not Confirm+Cancel | **204+409** (race) or **204+422** (serialised). Two 204s means you used Cancel after Confirm (legal). | `OrderConfiguration` xmin → middleware 409. Guaranteed 409: `dotnet test --filter xmin_concurrency_token_rejects_a_stale_write` |
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

### 3a. Board (the break — do not expect this on the API)

On paper: restaurant Confirm and customer Cancel, same instant, **no** version check. Both read `Created`, both write, last write wins. That is t1–t6. The running API already has `xmin`, so you **cannot** reproduce two successful writes live.

### 3b. Prove the fix — two **Confirmed** PATCHes on one new order

The **story** on the board is Confirm vs Cancel. Do **not** use that pair on the API for the race: `Created → Confirmed → Cancelled` is a **legal** sequence, so two 204s means you ran them **one after the other** (`Start-Job` is too slow — it is not a race).

Live HTTP uses **two Confirmed**. That cannot both succeed.

**1.** API running. Repo root.

**2.** New order, copy `"id"`:

```powershell
curl.exe -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
```

**3.** Same process, true overlap (not Start-Job):

```powershell
.\docs\runbooks\race-status.ps1 -OrderId PASTE_ID
```

| Codes | Meaning |
|---|---|
| **204** and **409** | Race. `xmin` caught it. Best case. |
| **204** and **422** | Second ran after commit. `Confirmed → Confirmed` is illegal. Still no lost update. |
| **Two 204s** | Should not happen with two Confirmed. New order; run the script again. |

Guaranteed **409** (two DbContexts, no HTTP timing luck):

```powershell
dotnet test Tadka.slnx --filter xmin_concurrency_token_rejects_a_stale_write --nologo
```

This is **`xmin` on the order**. Orders do **not** use `FOR UPDATE`.

**Pessimistic primitives** — toy, own table, two terminals. Postgres must be up. Full doc: [`toydemo/day-04-locking/locking-toy/RUN-AND-TEST.md`](../../toydemo/day-04-locking/locking-toy/RUN-AND-TEST.md).

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
- [ ] Two ids without a key; 201 then 200 with a key
- [ ] You can explain 409 vs 422
- [ ] Two-window PATCH → 204 + 409; toy `wait` freezes ~5s
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
