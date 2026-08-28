# Why this contract looks like this (Day 3)

Read this next to [`openapi-v1.yaml`](openapi-v1.yaml). The YAML is *what*. This file is *why*. Path vs query vs body vs header: [`where-data-goes.md`](where-data-goes.md).

It matches **tadka `day-03`** only. Later days add keys, 409, events, tracking. Do not pretend they are here.

Open the YAML in [Swagger Editor](https://editor.swagger.io) or VS Code. With the API running in Development you also get a generated doc at `/openapi/v1.json` (Scalar UI). That generated file has no reasoning.

---

## How to read a contract

Before any route, five questions. If you skip them you get `POST /CreateOrder` with a client-sent price.

1. **Capability.** What is the business doing? Place order. 86 a dish. Close a restaurant. Not "update row".
2. **Resource and verb.** Path is a noun. Method is the action. `POST /api/v1/orders`, not `POST /CreateOrder`.
3. **Trust.** What may the client assert? Identity of *what they want*. Never the price, never the line name after the fact, never "I am Delivered".
4. **Response.** 201 with a body when a resource is born. 204 when a partial update has nothing new to say. Snapshot fields on an order line because the menu will change.
5. **Failure.** Shape wrong → **400**. Resource missing → **404**. Shape fine, business says no → **422**. Same RFC 7807 body so one client handler.

Two layers of check (ADR-007): FluentValidation is "can I parse this?". Domain / factory / state machine is "am I allowed to?". Those are not the same HTTP code.

Money is always `{ amount, currency }`, never a naked decimal. Address is a value object flattened onto the row (no `money` table, no join).

No `PUT` (every update we do is partial; omitted field on PUT would blank data). No `DELETE` (orders and restaurants are history; deactivate with `PATCH { isActive: false }`).

`/api/v1` is in the URL before the first mobile client exists (ADR-010). Cheap now, expensive after.

Health is **not** under `/api/v1`. Liveness must not depend on Postgres.

---

## Health

### `GET /health`

| | |
|---|---|
| Capability | Is this process up? Orchestrators and load balancers ask this to know whether to send traffic to a *dead* box. |
| Why not `/api/v1` | Not a product resource. Not versioned with the contract. |
| Why no DB | If Postgres is down, you still want the process marked alive so you can *see* the 503 on ready, not a crash-loop. |
| Response | `{ status, timestamp }` only. No `database` field. If you see one, you are on an old controller. |

### `GET /health/ready`

| | |
|---|---|
| Capability | Can this instance do work? `SELECT 1` against Postgres. |
| 200 vs 503 | Connected vs disconnected. Same JSON shape, different `status` / `database`. |
| Rejected | Folding this into `/health`. Then a DB blip looks like the app is dead. |

---

## Restaurant catalogue

### `GET /api/v1/restaurants`

| | |
|---|---|
| Capability | Browse the catalogue. Optional `city`. |
| Why paged | Lists grow. `pageSize` clamped 1–50 so a client cannot dump the table. |
| Why no menu on this payload | List is names and addresses. Menu is a second resource. One fat payload would mix two cache lives (list changes slowly; 86'd dishes change at lunch). |
| Why no cross-schema join to orders | ADR-008. Catalogue does not know about bills. |

### `GET /api/v1/restaurants/{id}`

Same resource, one row. **Still no menu.** 404 if the id is unknown. We do not return 200 with `null`.

### `POST /api/v1/restaurants`

| | |
|---|---|
| Verb | `POST` because the server assigns the id (`gen_random_uuid`). Client does not choose the identity. |
| Trust | Name, address, prep time. Server sets `isActive: true` and `createdAt`. |
| 201 | Body plus `Location` of the GET. You can fetch what you just created. |
| 400 | Empty name, bad pincode (must be 6 digits), missing address. Shape. |

### `PATCH /api/v1/restaurants/{id}`

| | |
|---|---|
| Capability | Rename, change prep time, **or close the shop**. |
| Why not `DELETE` | Two years of orders still name this restaurant. Hard delete blanks history, GST, disputes. Cross-schema FKs are banned, so the database would not even stop you. |
| Why not `DELETE` with a soft-delete flag | The verb would lie. Client thinks it is gone; it is not. |
| Why `PATCH` not `PUT` | You never send the whole restaurant. `PUT` with a missing `phone` would blank it. |
| 204 | Nothing the client does not already know. |

Rejected: `POST /deleteRestaurant`. That is CRUD-as-RPC.

---

## Menu (partner edits, live price)

Live price lives **here**. The order copies it at place-order time (ADR-009). A later `PATCH` of price does not rewrite Tuesday's bill.

### `GET /api/v1/restaurants/{id}/menu`

Nested under the restaurant because a dish without a restaurant is not a Tadka concept in this slice. Filters `category` and `vegOnly` are query params (they are not a new resource). **Not paged** on Day 3: 16 seed items. Revisit when a menu is hundreds of rows.

404 if the restaurant id is wrong (not an empty array pretending the restaurant exists).

### `POST /api/v1/restaurants/{id}/menu`

Partner adds a dish. **Price is required here** because this *is* the catalogue. `isAvailable` starts true. 201 with the item. 400 if amount ≤ 0.

### `PATCH /api/v1/restaurants/{id}/menu/{itemId}`

Raise Chicken Biryani ₹299 → ₹320. Partial. Old orders keep `unitPrice` 299 and name "Chicken Biryani".

### `PATCH .../availability`

86 the dish without sending name, price, category. Small capability, small body `{ isAvailable }`. Place-order will 422 if someone still sends that `menuItemId`.

---

## Place and read orders

### `POST /api/v1/orders`

This is the Day 3 headline.

| | |
|---|---|
| Capability | Place an order. Not "insert into orders". |
| Path | `POST /api/v1/orders`. Collection, server-made id. |
| Body | `customerId`, `restaurantId`, `items[{ menuItemId, quantity, specialInstructions }]`, `deliveryAddress`. |
| **Not in the body** | `price`, `unitPrice`, `totalAmount`, line `name`. Client is not trusted. Parameter tampering is F12 → ₹1 biryani. Stale screen is Priya's 25-minute-old menu, no attacker, no error log, CA finds it. |
| Server does | Load restaurant + menu. Reject unknown restaurant **404**. Reject item not on *this* menu or unavailable **422**. Copy name and `unitPrice` onto each line. `totalAmount = sum(unitPrice × qty)`. Status `Created`. |
| 201 | Body includes the snapshot and the total (598.00 for two seed biryanis). `Location` is GET by id. |
| 400 | Empty items, qty 0, missing address, pincode not 6 digits. |
| Why snapshot | Receipt is history. Menu rename or ₹349 next week must not rewrite last Tuesday. |

Rejected: client sends `totalAmount` and server "verifies". Secure, and it dumps the restaurant's price change onto Priya as a pop-up. Option we took: client says *what*, server says *how much*.

`customerId` is a Guid with **no FK** to `identity.users` (ADR-008). Day 3 does not 404 a random customer id. That is a known looseness: validation at the app boundary later, not a cross-schema constraint today.

### `GET /api/v1/orders/{id}`

The bill. Lines carry **copied** name and unit price. `restaurantId` only: no join to `restaurant.restaurants` for the display name. Two queries in one process are allowed; a SQL JOIN across schemas is not.

### `GET /api/v1/orders`

Newest first. Optional `customerId`. Same paging clamp. For "my orders", not a warehouse dump.

---

## Lifecycle (airport, not a text column)

Status is a finite state machine on `Order`, not `order.Status = "Delivered"` from the controller.

```
Created → Confirmed | Cancelled
Confirmed → Preparing | Cancelled
Preparing → ReadyForPickup
ReadyForPickup → PickedUp
PickedUp → Delivered
Delivered, Cancelled, Refunded → (none)
```

`Refunded` is in the enum so Day 7 payment can use it. **No Day 3 transition writes it.**

### `PATCH /api/v1/orders/{id}/status`

| | |
|---|---|
| Capability | Kitchen / rider moves the order along a **legal** edge. |
| Why PATCH | Partial: only status. |
| 204 | Client already knows the target status it sent. |
| 404 | Unknown id. |
| 422 | `Created` → `Delivered` (illegal). Also an unknown string such as `delivred`: `Enum.TryParse` fails → `DomainException` → **422**, not 400. The JSON parsed; the value is not a business status. |
| Rejected | 400 for illegal jumps. Retry libraries treat 400 as "never send this again". The request was fine; the world was not in that state. |

### `POST /api/v1/orders/{id}/cancel`

| | |
|---|---|
| Capability | Cancel. Side effects later (refund, stop kitchen). Not a field flip. |
| Why POST not `PATCH { status: Cancelled }` | Cancel is a business action with a reason and timestamps. Generic PATCH would hide that. Same idea as waiter vs kitchen: expose the capability. |
| Allowed from | `Created` or `Confirmed` only. Preparing and later → 422. |
| Body | `{ reason }` optional. |

---

## Error shape (every failure)

`Content-Type: application/problem+json`

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
  "title": "Validation Failed",
  "status": 400,
  "detail": "One or more validation errors occurred.",
  "errors": { "Items": ["Order must have at least one item."] }
}
```

| Code | Who | Example |
|---|---|---|
| 400 | FluentValidation | `items: []` |
| 404 | Missing row | Unknown restaurant or order id |
| 422 | Factory or FSM or bad status token | Unavailable dish; Created→Delivered |
| 503 | Ready only | Postgres down |

Machines (load balancer, retry library) read **the number**. Humans read `title` / `detail`. Do not branch on the English string.

---

## What this spec deliberately omits

Day 4+: `Idempotency-Key`, `xmin` → 409, in-process events. Day 6+: cache, SSE. Day 8+: Payment. They are not missing from Day 3 by accident. They are not earned yet.

If you add a field, add a *why* here in the same five questions. If you cannot, it does not belong in v1.
