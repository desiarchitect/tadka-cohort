# Day 4 — Runbook: idempotency, 409, domain events

**Branch:** `day-04`. **What’s new:** `Idempotency-Key`, `xmin` → 409, in-process `OrderConfirmed` handler (log line). Saturday’s API stays.

> **Windows PowerShell:** `curl.exe`. Export `$BODY` **before** Beat 1.

| Thing | Value |
|-------|--------|
| API | `http://localhost:5224` |
| Compose service | `postgres` (container `tadka-postgres`) |
| Postgres | `localhost:5432`, db/user `tadka`, password `tadka_local` |

`down -v` when switching onto this branch. Service name is **`postgres`**, not `db`.

Seed GUIDs (same as Day 3): Meghana `a1b2c3d4-0001-4000-8000-000000000001`, biryani `b1b2c3d4-0001-4000-8000-000000000001`, Priya `c1b2c3d4-0001-4000-8000-000000000001`.

## 0. Fresh volume + tests (pre-class)

```bash
git checkout day-04
dotnet build Tadka.slnx
docker compose down -v
docker compose up -d
dotnet test Tadka.slnx
dotnet run --project src/Tadka.Api
```

**Look for:** tests **23/23**. Migration `Day04Hardening`. Listen **5224**. Docker Desktop must be running (Testcontainers).

## 1. Body variable

PowerShell:

```powershell
$BODY = '{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"a1b2c3d4-0001-4000-8000-000000000001","items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":2}],"deliveryAddress":{"line1":"302, Prestige Lakeside","line2":"Whitefield","city":"Bangalore","pincode":"560066","latitude":12.97,"longitude":77.75}}'
```

## 2. Beat 1 — double tap (live)

Two POSTs, **no** key → **two different** order ids.

Same `Idempotency-Key` twice → first **201**, second **200**, **same** id.

## 3. Beat 2 — lost update (draw, do not race the API)

The branch already has `xmin`. Two concurrent PATCHes will **not** show last-write-wins. Draw t1–t6 on the board. Loser is **409**, not 422.

## 4. Beat 3 — SMS after commit (live)

`PATCH …/status` `Confirmed`. Watch the **`dotnet run` log**, not only HTTP:

```
Notification: order … confirmed
```

Handler ran after commit. A failed SMS must not un-confirm.

## Done when

- [ ] 23/23 tests
- [ ] Two ids without a key; 201 then 200 with a key
- [ ] You can explain 409 vs 422
- [ ] Confirm prints the notification line

## Troubleshooting

| Symptom | What to do |
|---------|------------|
| Two POSTs with a key still create two orders | Header name is `Idempotency-Key`. Same key both times. |
| `dotnet test` hangs / Docker errors | Start Docker Desktop; Testcontainers needs it. |
| No notification in the log | Confirm a **Created** order; look at the API process terminal. |
| PowerShell `$BODY` empty | `curl` alias vs `curl.exe`. |

Next: Day 5 — indexes, pool, replica. Not Sunday.
