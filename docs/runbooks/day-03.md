# Day 3 — Runbook: The Monolith REST API (`/api/v1`)

**Branch:** `day-03`  ·  **What's new:** the full client-facing REST surface — **14 endpoints** under `/api/v1` (restaurants, menu, orders), **server-side pricing**, the **order state machine** (illegal transition → `422`), and **RFC 7807** error responses. Still one app + one Postgres (no cache/replica yet).

> New here? Read [`README.md`](README.md) (ports, seed IDs, the Windows-curl note). On Windows PowerShell use `curl.exe`, or just drive everything from the **Scalar UI** at `https://localhost:7036/scalar/v2`.

## 1. Run it

```bash
git checkout day-03
docker compose up -d
dotnet run --project src/Tadka.Api      # migrates + seeds 3 restaurants, 16 items, 1 customer
curl http://localhost:5224/health        # 200 Healthy
```

## 2. The 14 endpoints

| Domain | Endpoints |
|---|---|
| Restaurant | `GET /restaurants` · `GET /restaurants/{id}` · `POST /restaurants` · `PATCH /restaurants/{id}` (update/deactivate) · `GET /restaurants/{id}/menu` · `POST /restaurants/{id}/menu` · `PATCH /restaurants/{id}/menu/{itemId}` · `PATCH /restaurants/{id}/menu/{itemId}/availability` |
| Order | `POST /orders` · `GET /orders/{id}` · `GET /orders` (customer filter + paging) · `PATCH /orders/{id}/status` · `POST /orders/{id}/cancel` |
| Health | `GET /health` |

> No `PUT`, no `DELETE` by design: every update is partial (**PATCH**); nothing is hard-deleted — a restaurant is *deactivated*, an item *made unavailable*, an order *cancelled*. A system with order history never loses rows.

## 3. Browse (read) the seed data

```bash
curl -s http://localhost:5224/api/v1/restaurants
curl -s http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001
curl -s http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu
# filter the menu:
curl -s "http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu?vegOnly=true"
```

## 4. Place an order — server computes the price (the key demo)

The client sends **item ids + quantities, never a price**. The server looks up the menu and computes the total.

```bash
curl -s -X POST http://localhost:5224/api/v1/orders \
  -H "Content-Type: application/json" \
  -d '{
    "customerId": "c1b2c3d4-0001-4000-8000-000000000001",
    "restaurantId": "a1b2c3d4-0001-4000-8000-000000000001",
    "items": [
      { "menuItemId": "b1b2c3d4-0001-4000-8000-000000000001", "quantity": 2 }
    ],
    "deliveryAddress": {
      "line1": "302, Prestige Lakeside", "line2": "Whitefield",
      "city": "Bangalore", "pincode": "560066", "latitude": 12.97, "longitude": 77.75
    }
  }'
```
Expected **`201 Created`** — note `totalAmount.amount` is **598.00** (2 × ₹299, computed server-side), `status` is `"Created"`. Copy the returned `id` for the next steps.

```bash
ORDER=<paste-the-id>
curl -s http://localhost:5224/api/v1/orders/$ORDER                                  # the order
curl -s "http://localhost:5224/api/v1/orders?customerId=c1b2c3d4-0001-4000-8000-000000000001&page=1&pageSize=10"   # paged history
```

## 5. The order state machine (legal vs illegal)

Legal path `Created → Confirmed → Preparing → ReadyForPickup → PickedUp → Delivered`:

```bash
curl -s -X PATCH http://localhost:5224/api/v1/orders/$ORDER/status \
  -H "Content-Type: application/json" -d '{"status":"Confirmed"}' -w "%{http_code}\n"   # 204
curl -s -X PATCH http://localhost:5224/api/v1/orders/$ORDER/status \
  -H "Content-Type: application/json" -d '{"status":"Preparing"}' -w "%{http_code}\n"   # 204
```

Illegal jump (skip states) on a **new** order → **`422`** (domain rule, not a 400):
```bash
NEW=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" \
  -d '{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"a1b2c3d4-0001-4000-8000-000000000001","items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":1}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.9,"longitude":77.7}}' | sed -E 's/.*"id":"([^"]+)".*/\1/')
curl -s -X PATCH http://localhost:5224/api/v1/orders/$NEW/status \
  -H "Content-Type: application/json" -d '{"status":"Delivered"}' -w "\n%{http_code}\n"
# → 422 "Invalid State Transition"  (Created cannot jump straight to Delivered)
```

## 6. The error contract (RFC 7807) — every error, one shape

```bash
curl -s http://localhost:5224/api/v1/orders/00000000-0000-0000-0000-000000000000 -w "\n%{http_code}\n"   # 404 Resource Not Found
curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d '{"items":[]}' -w "\n%{http_code}\n"   # 400 Validation Failed (errors map)
# 422 = ordering an item that isn't on the chosen restaurant's menu (domain rule):
curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" \
  -d '{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"a1b2c3d4-0002-4000-8000-000000000002","items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":1}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.9,"longitude":77.7}}' -w "\n%{http_code}\n"   # 422
```
**Status-code taxonomy:** `400` = malformed request (validation) · `404` = not found · `422` = valid request that breaks a domain rule.

## 7. Restaurant-partner edits (PATCH, not PUT/DELETE)

```bash
# raise a dish's price (Money is always {amount, currency})
curl -s -X PATCH http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu/b1b2c3d4-0001-4000-8000-000000000001 \
  -H "Content-Type: application/json" -d '{"price":{"amount":320}}' -w "%{http_code}\n"   # 204
# deactivate a restaurant (our "delete")
curl -s -X PATCH http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001 \
  -H "Content-Type: application/json" -d '{"isActive":false}' -w "%{http_code}\n"          # 204
```

## ✅ Done when

- [ ] `GET /api/v1/restaurants` returns the 3 seeded restaurants; menu returns items.
- [ ] `POST /orders` returns `201` with a **server-calculated** `totalAmount` (₹598 for 2× biryani).
- [ ] `PATCH /orders/{id}/status` advances legally (`204`); an illegal jump returns **`422`**.
- [ ] Unknown id → `404`; empty/bad body → `400` (with an `errors` map); wrong-restaurant item → `422` — all in RFC 7807 shape.
- [ ] Price edit → `204`, and a subsequent `GET …/menu` shows the new price.

➡️ Next: [day-04.md](day-04.md) — make this flow survive retries and races.
