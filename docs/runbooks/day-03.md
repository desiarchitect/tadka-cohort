# Day 3 — Runbook: REST API, server-side pricing, RFC 7807

**Branch:** `day-03` · **What's new:** `/api/v1` restaurants + orders, `OrderFactory` prices from the menu, order state machine, RFC 7807, seed (3 restaurants / 16 items / Priya). Day 2 schemas and Day 1 `/health` + `/health/ready` stay.

> **Windows PowerShell:** use **`curl.exe`** (plain `curl` is `Invoke-WebRequest`). From the repo root. Quote `@file` so PowerShell does not splat: `--data-binary "@docs/runbooks/place-order.json"`. Do not paste `-d "{...}"` into PowerShell — it mangles JSON.

> **Contract:** [`docs/api/openapi-v1.yaml`](../api/openapi-v1.yaml), [`docs/api/contract-reasoning.md`](../api/contract-reasoning.md), [`docs/api/where-data-goes.md`](../api/where-data-goes.md) (path vs query vs body vs header; HTTP QUERY is named, not implemented). Dev also serves generated OpenAPI at `/openapi/v1.json` and Scalar at `/scalar`.

| Thing | Value |
|-------|--------|
| API (http) | `http://localhost:5224` |
| Scalar (Dev) | `http://localhost:5224/scalar` |
| OpenAPI JSON (Dev) | `http://localhost:5224/openapi/v1.json` |
| Compose service | `postgres` (container `tadka-postgres`) |
| Postgres | `localhost:5432`, db `tadka`, user `tadka`, password `tadka_local` |

### pgAdmin (or any GUI) from your machine

Host `localhost` (or `127.0.0.1`), port `5432`, database `tadka`, user `tadka`, password `tadka_local`. **Do not** use Host `tadka-postgres`. After `dotnet run`, schemas `ordering` / `restaurant` / … have seed rows.

### Docker — commands in this file

| Command | What it does |
|---------|----------------|
| `docker compose down -v` | Stop and **wipe** the volume. Required when switching onto `day-03` so seed migrations apply. |
| `docker compose up -d` | Start Postgres in the background. |
| `docker compose ps` | Want `tadka-postgres` **`(healthy)`**. |
| `docker compose stop postgres` | Pause the DB. Data stays. Liveness vs ready demo. |
| `docker compose start postgres` | Resume. Wait for `(healthy)`. |

Service name is **`postgres`**, container **`tadka-postgres`**. `docker compose stop db` fails.

**Container name already in use:** another clone already started `tadka-postgres`. `docker compose down` there, then `up -d` here.

Stable seed GUIDs (clipboard / sticky note):

| | |
|--|--|
| Meghana Foods | `a1b2c3d4-0001-4000-8000-000000000001` |
| Chicken Biryani (₹299) | `b1b2c3d4-0001-4000-8000-000000000001` |
| Priya Sharma | `c1b2c3d4-0001-4000-8000-000000000001` |

---

## 0. Fresh volume

A Day-2 volume is missing the seed migration. Always:

```bash
git checkout day-03
dotnet build Tadka.slnx
docker compose down -v
docker compose up -d
docker compose ps
```

**Look for:** `tadka-postgres` **`(healthy)`**. Build: **0 errors**.

---

## 1. Run the API

```bash
dotnet run --project src/Tadka.Api
```

**Look for:** applying `InitialDomainModel` then `OrderLifecycleAndDemoSeed`; listening on **5224**.

---

## 2. Health is still split

```bash
curl.exe -s http://localhost:5224/health
# { "status": "Healthy", "timestamp": "..." }   — no database field

curl.exe -s http://localhost:5224/health/ready
# { "status": "Healthy", "database": "Connected", ... }
```

---

## 3. Browse (seed)

```bash
curl.exe -s http://localhost:5224/api/v1/restaurants
curl.exe -s http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu
```

**Look for:** Meghana Foods, Truffles, Vidyarthi Bhavan. Menu has 16 items across the three. Chicken Biryani **299**.

---

## 4. Place an order — no price in the request

Body file: [`place-order.json`](place-order.json) (Priya, Meghana, 2× Chicken Biryani, no price). Quote the `@` path.

```bash
curl.exe -s -w "\nHTTP %{http_code}\n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/place-order.json"
```

**Look for — HTTP 201.** `totalAmount.amount` is **598.00**. Request had no price. Save the order `id`.

---

## 5. State machine and error shape

Statuses (exact strings, case-insensitive): `Created`, `Confirmed`, `Preparing`, `ReadyForPickup`, `PickedUp`, `Delivered`, `Cancelled`, `Refunded`.

Happy path is **one step at a time**. Skip a step → **422**. `Refunded` is in the enum for Day 7 payments; nothing transitions into it today.

| From | Legal next (PATCH `/status`) | Notes |
|------|------------------------------|--------|
| Created | Confirmed, Cancelled | POST places the order here |
| Confirmed | Preparing, Cancelled | Kitchen accepted |
| Preparing | ReadyForPickup | Cannot cancel once cooking |
| ReadyForPickup | PickedUp | Rider collected |
| PickedUp | Delivered | |
| Delivered | *(none)* | Terminal |
| Cancelled | *(none)* | Terminal |
| Refunded | *(none)* | Terminal; not reachable on Day 3 |

Cancel as a **capability** (reason + `cancelledAt`): `POST /api/v1/orders/<id>/cancel` with `{"reason":"..."}`. Same gate: only Created or Confirmed. After Preparing, cancel is 422.

Walk the order from step 4. Replace `<ORDER_ID>`. Legal PATCH is **204**; skip or late cancel is **422**.

```bash
curl.exe -s -w "\nHTTP %{http_code}\n" -X PATCH http://localhost:5224/api/v1/orders/<ORDER_ID>/status -H "Content-Type: application/json" --data-binary '{"status":"Confirmed"}'

# skip kitchen → 422 (allowed from Confirmed: Preparing, Cancelled)
curl.exe -s -w "\nHTTP %{http_code}\n" -X PATCH http://localhost:5224/api/v1/orders/<ORDER_ID>/status -H "Content-Type: application/json" --data-binary '{"status":"Delivered"}'

curl.exe -s -w "\nHTTP %{http_code}\n" -X PATCH http://localhost:5224/api/v1/orders/<ORDER_ID>/status -H "Content-Type: application/json" --data-binary '{"status":"Preparing"}'

# cooking has started → cancel is 422
curl.exe -s -w "\nHTTP %{http_code}\n" -X POST http://localhost:5224/api/v1/orders/<ORDER_ID>/cancel -H "Content-Type: application/json" --data-binary '{"reason":"too late"}'

curl.exe -s -w "\nHTTP %{http_code}\n" -X PATCH http://localhost:5224/api/v1/orders/<ORDER_ID>/status -H "Content-Type: application/json" --data-binary '{"status":"ReadyForPickup"}'
curl.exe -s -w "\nHTTP %{http_code}\n" -X PATCH http://localhost:5224/api/v1/orders/<ORDER_ID>/status -H "Content-Type: application/json" --data-binary '{"status":"PickedUp"}'
curl.exe -s -w "\nHTTP %{http_code}\n" -X PATCH http://localhost:5224/api/v1/orders/<ORDER_ID>/status -H "Content-Type: application/json" --data-binary '{"status":"Delivered"}'

curl.exe -s http://localhost:5224/api/v1/orders/<ORDER_ID>
# status is Delivered; deliveredAt is set. Further PATCH → 422 (terminal).
```

Cancel while still Created — place a **second** order (step 4 again):

```bash
curl.exe -s -w "\nHTTP %{http_code}\n" -X POST http://localhost:5224/api/v1/orders/<NEW_ORDER_ID>/cancel -H "Content-Type: application/json" --data-binary '{"reason":"changed my mind"}'
# 204; GET that order → status Cancelled, cancellationReason set

# unknown id → 404
curl.exe -s -w "\nHTTP %{http_code}\n" http://localhost:5224/api/v1/orders/00000000-0000-0000-0000-000000000000

# empty items → 400 with an errors map
curl.exe -s -w "\nHTTP %{http_code}\n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" --data-binary "@docs/runbooks/empty-items.json"
```

**Look for:** 204 on each legal step; 422 on a skip or late cancel; 404 / 400. Bodies are RFC 7807 (`type`, `title`, `status`, `detail`). 400 has `errors`. 422 `detail` lists the allowed next states.

---

## 6. Partner edit — PATCH, not PUT

```bash
curl.exe -s -w "\nHTTP %{http_code}\n" -X PATCH http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu/b1b2c3d4-0001-4000-8000-000000000001 -H "Content-Type: application/json" --data-binary '{"price":{"amount":320}}'

curl.exe -s http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu

curl.exe -s -w "\nHTTP %{http_code}\n" -X PATCH http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001 -H "Content-Type: application/json" --data-binary '{"isActive":false}'
```

**Look for:** 204, then Chicken Biryani **320**. Restaurant deactivate is PATCH `isActive`, not DELETE.

---

## 7. (Optional) liveness still ignores Postgres

```bash
docker compose stop postgres
curl.exe -s -w "\nHTTP %{http_code}\n" http://localhost:5224/health
curl.exe -s -w "\nHTTP %{http_code}\n" http://localhost:5224/health/ready
docker compose start postgres
```

**Look for:** `/health` **200**; `/health/ready` **503**.

---

## Done when

- [ ] `dotnet build Tadka.slnx` is clean.
- [ ] `down -v` then `up -d` → postgres healthy.
- [ ] `dotnet run` applies both migrations; restaurants = 3.
- [ ] POST order with no price → 201 / **598.00**.
- [ ] PATCH the same order Created → Confirmed → Preparing → ReadyForPickup → PickedUp → Delivered, each **204**.
- [ ] Skip (Created → Delivered) → 422; cancel after cooking starts → 422; missing id → 404; empty items → 400; same problem+json shape.
- [ ] PATCH price then GET menu shows the new amount.
- [ ] `/health` has no `database` field.

## Troubleshooting

| Symptom | What to do |
|---------|------------|
| `relation already exists` | `docker compose down -v && docker compose up -d`, then `dotnet run`. |
| Empty restaurants | You are on Day 2 volume or Day 2 code. This branch must apply `OrderLifecycleAndDemoSeed`. |
| `/health` returns `database` | Old controller. Day 3 keeps the Day 1/2 split. |
| `curl` HTML / method error | Use `curl.exe` on PowerShell (`curl` is `Invoke-WebRequest`). |
| POST 400 on a good body | PowerShell ate the JSON. Use `--data-binary "@docs/runbooks/place-order.json"` (quotes around `@path`). |
| `--data-binary @file` splat error | PowerShell treated `@` as splatting. Quote it: `"@docs/runbooks/place-order.json"`. |

➡️ Next: Day 4 — order-flow hardening (idempotency, concurrency).
