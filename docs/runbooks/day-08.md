# Day 8 — Runbook: extract Payment (fault, not speed)

**Branch:** `day-08`. Day 7 fixed **latency in one process**. Today Payment is `Tadka.Payment.Api` on **`:5240`** with **`payment-db` `:5434`**. Driver: **fault isolation, PCI scope, data ownership** — not “it was slow.” ADRs 024–026.

**Two apps, four containers.** Wound at the end stays **open** (Week 5 Kafka). Do not “fix” it.

> **Windows:** `curl.exe`. Quote `@file`. Two terminals for the two `dotnet run`s.

| Thing | Value |
|---|---|
| Monolith | `http://localhost:5224` |
| Payment | `http://localhost:5240` |
| payment-db | `localhost:5434`, db `tadka_payment` |
| POST body | `@docs/runbooks/place-order.json` |

### Demo → code

| When | What you run | Look for | Code |
|---|---|---|---|
| Happy path | POST, wait, GET both | order `Confirmed`; `:5240/payments/{id}` `Completed` | `HttpPaymentClient` |
| Isolation | Ctrl+C Payment | menu **200**, health **200**, POST **201** in ms | two processes |
| Wound | GET that order after Payment restart | still **`Created`**. New POST **does** Confirm | in-memory queue consumed, HTTP failed |
| Own DB | `stop payment-db` | menu still **200** | ADR-026 |
| Grep | Select-String **excluding Migrations** | no `FakePaymentGateway` / live `PaymentDbContext` | `BoundaryTests.cs` |

---

## How it is wired

```
POST :5224/orders  →  Channel  →  PaymentProcessor
                                      │ HTTP
                                      ▼
                               :5240/payments   →  tadka_payment (5434)
```

| Job | Tadka (.NET) | Java | Node |
|---|---|---|---|
| Typed client | `IPaymentClient` + `HttpClient` | OpenFeign | axios wrapper |
| Timeout/bulkhead | same Polly pipeline, now around the **network hop** | Resilience4j | Opossum |
| Own database | `Tadka.Payment.Api` migrates `tadka_payment` | separate Spring datasource | separate Prisma schema |

---

## 0. Fresh start

```powershell
git checkout day-08
docker compose down -v
docker compose up -d
docker compose ps    # four healthy: postgres, replica, redis, payment-db
```

**Terminal 1**

```powershell
dotnet run --project src/Tadka.Payment.Api    # :5240
```

**Terminal 2**

```powershell
dotnet run --project src/Tadka.Api            # :5224
```

```powershell
curl.exe -s http://localhost:5240/health    # {"status":"Healthy","service":"payment"}
curl.exe -s -w " HTTP %{http_code}`n" http://localhost:5224/health
```

Each app migrates **its own** database.

```powershell
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT tablename FROM pg_tables WHERE schemaname='payment';"
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT tablename FROM pg_tables WHERE schemaname='payment';"
```

**Look for:** payment-db has `payments`. Monolith payment schema may still **exist as an empty leftover** (0 tables). Teaching used to say “no payment schema” — **0 tables** is the proof. Data lives on **5434**.

`dotnet test` → **33 + 4** (monolith includes Day-6 leftovers + `BoundaryTests`).

---

## 1. Happy path — HTTP bridge (ADR-025)

```powershell
curl.exe -s -w "`nHTTP %{http_code} time=%{time_total}s`n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
```

Copy `"id"`. Wait 2 s.

```powershell
curl.exe -s http://localhost:5224/api/v1/orders/PASTE_ID
curl.exe -s http://localhost:5240/payments/PASTE_ID
```

**Captured:** POST 201 `Created` (cold ~1.3 s, then tens of ms). GET order **`Confirmed`**. Payment service JSON `"status":"Completed"`, `FAKEPAY-…`.

---

## 2. BREAK isolation — Payment process gone, monolith lives (ADR-024)

Ctrl+C **Terminal 1 only**.

```powershell
curl.exe -s -o NUL -w "menu HTTP %{http_code}`n" http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu
curl.exe -s -o NUL -w "health HTTP %{http_code}`n" http://localhost:5224/health
curl.exe -s -w "`nHTTP %{http_code} time=%{time_total}s`n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
```

**Identified (good):** menu **200**, health **200**, POST **201** in **~24 ms**, `"status":"Created"`. Day 7 in-process: a payment fatal shared the host.

Optional crash (not “I stopped it”): Payment with `$env:Payment__CrashOnCharge="true"` then POST — `Environment.FailFast` kills **Payment only**. Demo lever, not production.

---

## 3. The wound — charge vanished (ADR-025) — **do not fix**

Keep Payment **down**. Copy the id from §2 POST. Wait 3 s:

```powershell
curl.exe -s http://localhost:5224/api/v1/orders/PASTE_ID
```

**Look for:** still **`Created`**.

Restart Payment (`dotnet run --project src/Tadka.Payment.Api`). Wait 2 s. GET **the same id** again.

**Captured:** still **`Created`**. A **new** POST after Payment is up becomes **`Confirmed`**.

The background worker **dequeued** the work item, HTTP failed, item is gone. Not in the queue, not in payment-db. User got **201**. You hear this from customers, not from monitoring.

**Do not add retry in class.** Retry is also in memory. Week 5: Kafka + Outbox.

---

## 4. Own database (ADR-026)

Payment can be up. Then:

```powershell
docker compose stop payment-db
curl.exe -s -o NUL -w "menu HTTP %{http_code}`n" http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu
docker compose start payment-db
```

**Look for:** menu **200**. Day 7: Payment migrated during monolith startup — a payment-DB outage could block boot.

---

## 5. Grep the seam

Old EF snapshots still mention `Domain.Payments`. **Exclude Migrations** or the demo “fails.”

```powershell
Get-ChildItem src\Tadka.Api -Recurse -Filter *.cs |
  Where-Object { $_.FullName -notmatch '\\Migrations\\' } |
  Select-String "FakePaymentGateway|PaymentDbContext"
```

**Look for:** no `FakePaymentGateway`. `PaymentDbContext` only in comments if at all. Live client: `Modules\Payments\HttpPaymentClient.cs`. `tests/Tadka.Api.Tests/Architecture/BoundaryTests.cs` fails the build on a stray import.

---

## Done when

- [ ] Four containers; `:5240` and `:5224` health 200
- [ ] payment-db has `payments`; monolith payment schema has **0 tables**
- [ ] Happy path: order Confirmed + `:5240/payments/{id}` Completed
- [ ] Payment down: menu/health 200, POST 201 ms
- [ ] That order stays `Created` after Payment restarts; a new order Confirms
- [ ] `stop payment-db`: menu 200
- [ ] Grep excluding Migrations is clean
- [ ] `dotnet test` → **33 + 4**

## Troubleshooting

| Symptom | What to do |
|---|---|
| Order never Confirmed | Is `:5240` up? `Payment:ServiceUrl` = `http://localhost:5240` |
| Payment will not start | `payment-db` healthy on 5434 |
| Port in use | Payment **5240**, monolith **5224** |
| Grep hits Designer.cs | You included `Migrations`. Exclude them |
| `\dn` still lists `payment` | Empty leftover namespace. Check **tables**, not nspname |

**Do not** extract because Day 7 was slow. Slow is already fixed. Today is blast radius.

Next: Week 5 — Kafka + Outbox for **this** stuck order.
