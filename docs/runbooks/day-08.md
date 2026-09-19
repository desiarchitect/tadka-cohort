# Day 8 — Runbook (you run this)

You are on branch `day-08`. What changed since Day 7: [`docs/changelog.md`](../changelog.md).

Day 7 fixed **latency in one process** (timeout, bulkhead, async Channel). Today you **extract Payment into its own process**. The reason is **fault isolation, PCI scope, and its own database** — not “it was slow.” Slow is already fixed.

You will:

1. Run **two apps** and see an order still Confirm.
2. **Kill Payment** and see the monolith still take orders (isolation).
3. See that order **stay `Created` forever** (the wound — do **not** fix it today).
4. **Stop payment-db** and see the menu still work (own database).
5. **Grep** that Ordering no longer contains the gateway.

Wound stays open until Week 5 (Kafka + Outbox).

**Windows:** `curl.exe`. Quote `@file`. **Two terminals** — one per `dotnet run`. Prefer `--launch-profile http` so the monolith is **5224**.

| Thing | Value |
|---|---|
| Monolith | `http://localhost:5224` |
| Payment | `http://localhost:5240` |
| payment-db | `localhost:5434`, database `tadka_payment` |
| POST body | `@docs/runbooks/place-order.json` |
| Meghana menu | `a1b2c3d4-0001-4000-8000-000000000001` |

```
POST :5224/orders  →  Channel (still in the monolith)  →  HTTP  →  :5240/payments  →  tadka_payment (5434)
```

---

## 0. Start both apps

```powershell
git checkout day-08
docker compose down -v
```

`down -v` only removes containers **this folder** created. If you also use `D:\work\cohort\tadka`, names like `tadka-payment-db` can remain. If `up -d` says **name already in use**:

```powershell
docker rm -f tadka-postgres tadka-postgres-replica tadka-redis tadka-payment-db tadka-nginx-lb
```

```powershell
docker compose up -d
docker compose ps
```

**You want four healthy:** postgres, replica, redis, **payment-db**.

**Terminal 1 — Payment first** (it owns `:5434` migrations):

```powershell
dotnet run --project src/Tadka.Payment.Api
```

**You want:** listening on **http://localhost:5240**.

**Terminal 2 — monolith:**

```powershell
dotnet run --project src/Tadka.Api --launch-profile http
```

**You want:** listening on **http://localhost:5224**.

A **third** terminal for curls:

```powershell
curl.exe -s http://localhost:5240/health
# You want: "status":"Healthy" and "service":"payment"

curl.exe -s -w " HTTP %{http_code}`n" http://localhost:5224/health
# You want: HTTP 200
```

Each app migrates **its own** database. Check:

```powershell
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT tablename FROM pg_tables WHERE schemaname='payment';"
```

**You want:** table `payments` on **5434**.

```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT tablename FROM pg_tables WHERE schemaname='payment';"
```

**You want:** **0 tables** (or empty). The `payment` schema name may still exist on the monolith DB as a leftover namespace. **Data lives on 5434**, not in the monolith’s Postgres.

`dotnet test` → **33 + 4** (monolith + Payment).

---

## 1. Happy path — the HTTP bridge

**What you are proving:** extracting Payment did **not** change the customer path. POST still returns in **ms** (`Created`). A few seconds later the order is **Confirmed** and Payment has **Completed**. The Channel is still in the monolith; the charge now goes **over HTTP** to `:5240`.

**Before this POST:** both health checks **200**. If you still have Day 7 `$env:Payment__Mode=Synchronous` in a shell, use a **new** PowerShell for both `dotnet run`s.

```powershell
curl.exe -s -w "`nHTTP %{http_code} time=%{time_total}s`n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
```

**You want:** HTTP **201**, `"status":"Created"`, **time tens of ms** (first POST after boot can be ~1 s JIT). Copy `"id"`.

Wait 2 s, then:

```powershell
curl.exe -s http://localhost:5224/api/v1/orders/PASTE_ID
```

**You want:** `"status":"Confirmed"`.

```powershell
curl.exe -s http://localhost:5240/payments/PASTE_ID
```

**You want:** `"status":"Completed"` and a `FAKEPAY-…` reference. Same id, **other process, other database**.

If the order stays `Created`: Payment (`:5240`) is not running or not healthy.

---

## 2. BREAK — Payment process gone, monolith lives

**What you break:** Ctrl+C **Terminal 1 only** (Payment). Monolith stays up.

**What you are proving:** Day 7, a payment crash could take the **whole host**. Today menus and checkout **keep working**. Isolation is the point of extraction.

```powershell
curl.exe -s -o NUL -w "menu HTTP %{http_code}`n" http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu
```

**You want:** menu HTTP **200**.

```powershell
curl.exe -s -o NUL -w "health HTTP %{http_code}`n" http://localhost:5224/health
```

**You want:** health HTTP **200**.

```powershell
curl.exe -s -w "`nHTTP %{http_code} time=%{time_total}s`n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
```

**You want:** HTTP **201**, **time ~20–50 ms**, `"status":"Created"`. Copy this **id** — you need it in §3.

The order is accepted. The charge **cannot** complete because Payment is down. That is isolation **and** the start of the wound.

---

## 3. The wound — that charge is gone (do not fix)

**What failed:** the monolith worker **dequeued** the work item, HTTP to `:5240` failed, the item is **dropped**. Not in the Channel, not in payment-db. The customer already got **201**.

**What you do not do today:** retry, Kafka, Outbox. Those are Week 5. Retry in memory still dies if the process restarts.

Keep Payment **down**. Use the id from §2. Wait 3 s:

```powershell
curl.exe -s http://localhost:5224/api/v1/orders/PASTE_ID
```

**You want:** still **`Created`**.

**Terminal 1** — start Payment again:

```powershell
dotnet run --project src/Tadka.Payment.Api
```

Wait until `:5240/health` is 200. GET **the same id**:

```powershell
curl.exe -s http://localhost:5224/api/v1/orders/PASTE_ID
```

**You want:** **still `Created`**. Restarting Payment does **not** replay the lost HTTP call.

Prove the path works when Payment is up — **new** POST:

```powershell
curl.exe -s -w "`nHTTP %{http_code} time=%{time_total}s`n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
```

Wait 2 s, GET that **new** id. **You want:** `Confirmed`. The stuck order from §2 stays `Created`. That is the customer ticket you cannot close today.

---

## 4. Own database — stop payment-db, menu still works

**What you break:** `docker compose stop payment-db` (Postgres on **5434** only).

**What you are proving:** Payment’s data is **not** on the monolith DB. Day 7, Payment migrated during monolith startup — a payment-schema outage could block **boot**. Today the restaurant menu does not need 5434.

Payment can be up or down.

```powershell
docker compose stop payment-db
curl.exe -s -o NUL -w "menu HTTP %{http_code}`n" http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu
```

**You want:** menu HTTP **200**.

```powershell
docker compose start payment-db
```

**Reset** so Payment can migrate/run again. Wait until `docker compose ps` shows payment-db healthy.

---

## 5. Grep the seam

**What you are proving:** the gateway and `PaymentDbContext` **moved** to `Tadka.Payment.Api`. The monolith talks over HTTP (`HttpPaymentClient`). Old EF snapshots still mention `Domain.Payments` — **exclude Migrations** or grep “fails” for the wrong reason.

```powershell
Get-ChildItem src\Tadka.Api -Recurse -Filter *.cs |
  Where-Object { $_.FullName -notmatch '\\Migrations\\' } |
  Select-String "FakePaymentGateway|PaymentDbContext"
```

**You want:** no `FakePaymentGateway`. No live `PaymentDbContext` in the monolith. The client is `src\Tadka.Api\Modules\Payments\HttpPaymentClient.cs`.

---

## Done when

- [ ] Four containers; `:5240` and `:5224` health 200
- [ ] payment-db has `payments`; monolith `payment` schema has **0 tables**
- [ ] Happy path: order `Confirmed` + `:5240/payments/{id}` `Completed`
- [ ] Payment down: menu/health 200, POST 201 in ms
- [ ] That order stays `Created` after Payment restarts; a **new** order Confirms
- [ ] `stop payment-db`: menu 200
- [ ] Grep excluding Migrations is clean
- [ ] `dotnet test` → **33 + 4**

## If something looks wrong

| You see | Lesson or setup? | What you do |
|---|---|---|
| `name already in use` / tadka-payment-db | setup | `docker rm -f tadka-payment-db` then `up -d` |
| order never Confirmed | setup | is `:5240` up? |
| Payment will not start | setup | payment-db healthy on **5434** |
| port in use | setup | Payment **5240**, monolith **5224** |
| POST ~9 s + Confirmed on the POST | leftover Day 7 env | new PowerShell, **no** `$env:Payment__*` |
| grep hits `Designer.cs` | setup | you included `Migrations` |
| `\dn` still lists `payment` on monolith | leftover name | check **tables**, not schema name |
| stuck order becomes Confirmed after restart | you “fixed” the wound | that is not this branch — Channel drop is the lesson |

You did **not** extract because Day 7 was slow. Today is **blast radius**. The stuck `Created` order is the homework for Kafka.

Next: Week 5 — Kafka + Outbox for **this** order.
