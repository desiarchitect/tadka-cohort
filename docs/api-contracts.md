# Tadka API Contracts

> **This is a contract-first document.** It is designed and agreed *before* the controllers are written — the code implements this contract, not the other way around. Today these are in-process calls in one app; at extraction (Week 4+) the same contracts become the **network boundaries** between services. Design them as if a mobile team and a partner service already depend on them, because soon they will.

All endpoints live in ONE application: `Tadka.Api`, under the **`/api/v1`** prefix (see ADR-010 for the versioning decision). Grouped by domain for future service extraction.

Base URL: `https://localhost:7036` (dev) or `http://localhost:5224`

---

## Restaurant Endpoints

### GET /api/v1/restaurants

List restaurants with optional city filter and pagination.

**Query Parameters:**

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| city | string | — | Filter by city (case-insensitive) |
| page | int | 1 | Page number |
| pageSize | int | 10 | Items per page (max 50) |

**Response: 200 OK**

```json
{
  "items": [
    {
      "id": "a1b2c3d4-0001-4000-8000-000000000001",
      "name": "Meghana Foods",
      "address": {
        "line1": "75, 12th Main Road",
        "city": "Bangalore",
        "pincode": "560034"
      },
      "isActive": true,
      "avgPrepTimeMinutes": 25
    }
  ],
  "page": 1,
  "pageSize": 10,
  "totalCount": 3,
  "totalPages": 1
}
```

---

### GET /api/v1/restaurants/{restaurantId}

Get a single restaurant by ID.

**Response: 200 OK**

```json
{
  "id": "a1b2c3d4-0001-4000-8000-000000000001",
  "name": "Meghana Foods",
  "address": {
    "line1": "75, 12th Main Road",
    "city": "Bangalore",
    "pincode": "560034"
  },
  "isActive": true,
  "avgPrepTimeMinutes": 25
}
```

**Response: 404 Not Found** — See [error-standard.md](api-specs/error-standard.md)

---

### POST /api/v1/restaurants

Create a new restaurant.

**Request Body:**

```json
{
  "name": "Meghana Foods",
  "address": {
    "line1": "75, 12th Main Road",
    "line2": "Koramangala",
    "city": "Bangalore",
    "pincode": "560034",
    "latitude": 12.9352,
    "longitude": 77.6245
  },
  "avgPrepTimeMinutes": 25
}
```

**Validation Rules:**
- `name` is required
- `address.pincode` must be 6 digits

**Response: 201 Created**

```json
{
  "id": "a1b2c3d4-0001-4000-8000-000000000001",
  "name": "Meghana Foods",
  "address": {
    "line1": "75, 12th Main Road",
    "city": "Bangalore",
    "pincode": "560034"
  },
  "isActive": true,
  "avgPrepTimeMinutes": 25
}
```

**Response: 400 Bad Request** — Validation error, see [error-standard.md](api-specs/error-standard.md)

---

### PATCH /api/v1/restaurants/{restaurantId}

Update restaurant details (name, address, prep time) or **deactivate** it.

> **Why PATCH — and why no PUT or DELETE?** The update is *partial* (you change the prep time, not re-send the whole resource) → **PATCH**, not PUT. There is **no `DELETE`**: a restaurant with order history is never hard-deleted — you **deactivate** it (`isActive: false`) so past orders and audit trails survive. Deactivation *is* our delete.

**Request Body (any subset of fields):**

```json
{ "avgPrepTimeMinutes": 30, "isActive": false }
```

**Response: 204 No Content**
**Response: 404 Not Found** — Restaurant not found

---

### GET /api/v1/restaurants/{restaurantId}/menu

Get menu items for a restaurant with optional filters.

**Query Parameters:**

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| category | string | — | Filter by category |
| vegOnly | bool | false | Show only vegetarian items |

**Response: 200 OK**

```json
[
  {
    "id": "b1b2c3d4-0001-4000-8000-000000000001",
    "name": "Chicken Biryani",
    "description": "Hyderabadi style dum biryani with tender chicken pieces",
    "price": {
      "amount": 299.00,
      "currency": "INR"
    },
    "category": "Main Course",
    "isAvailable": true,
    "isVeg": false
  }
]
```

**Response: 404 Not Found** — Restaurant not found

---

### POST /api/v1/restaurants/{restaurantId}/menu

Add a menu item to a restaurant (the restaurant-partner flow — they manage their own menu).

> **Why POST here (nested)?** A menu item doesn't exist independently of its restaurant — it's created *within* the restaurant resource, so the create lives under `/restaurants/{id}/menu`. Creating a resource → **POST**.

**Request Body:**

```json
{
  "name": "Chicken 65",
  "description": "Spicy Hyderabadi fried chicken",
  "price": { "amount": 229.00, "currency": "INR" },
  "category": "Starters",
  "isVeg": false
}
```

**Response: 201 Created** — the created menu item
**Response: 404 Not Found** — Restaurant not found
**Response: 400 Bad Request** — Validation error

---

### PATCH /api/v1/restaurants/{restaurantId}/menu/{menuItemId}

Edit a menu item — **change its price**, description, category, veg flag, or availability.

> **Why this exists (the gap the contract review caught):** the product brief says restaurants manage *prices*. A restaurant raising biryani from ₹280→₹320 needs this. The edit is partial → **PATCH** (not PUT). To "remove" an item we set `isAvailable: false` (soft-hide) — no `DELETE`, because past orders snapshot the item and we keep the catalog for audit.

**Request Body (any subset):**

```json
{ "price": { "amount": 320.00, "currency": "INR" }, "isAvailable": false }
```

**Response: 204 No Content**
**Response: 404 Not Found** — Restaurant or menu item not found

---

### PATCH /api/v1/restaurants/{restaurantId}/menu/{menuItemId}/availability

Toggle menu item availability (e.g., out of stock during dinner rush).

> **Why a dedicated sub-route when the PATCH above can also set `isAvailable`?** This is the single most frequent kitchen action during dinner rush — a one-tap "86 this dish." A purpose-built, idempotent endpoint keeps the hot path simple and auditable, and lets us rate-limit/cache it independently later. The general menu PATCH covers everything else.

**Request Body:**

```json
{
  "isAvailable": false
}
```

**Response: 204 No Content**

**Response: 404 Not Found** — Restaurant or menu item not found

---

## Order Endpoints

### POST /api/v1/orders

Create a new order. Server calculates prices from menu items (client does NOT send prices).

**Request Body:**

```json
{
  "customerId": "c1b2c3d4-0001-4000-8000-000000000001",
  "restaurantId": "a1b2c3d4-0001-4000-8000-000000000001",
  "items": [
    {
      "menuItemId": "b1b2c3d4-0001-4000-8000-000000000001",
      "quantity": 2,
      "specialInstructions": "Extra spicy"
    },
    {
      "menuItemId": "b1b2c3d4-0003-4000-8000-000000000003",
      "quantity": 1
    }
  ],
  "deliveryAddress": {
    "line1": "302, Prestige Lakeside Habitat",
    "line2": "Whitefield",
    "city": "Bangalore",
    "pincode": "560066",
    "latitude": 12.9698,
    "longitude": 77.7500
  }
}
```

**Validation Rules:**
- `items` must have at least one element
- `items[].quantity` must be > 0
- `deliveryAddress` is required
- `deliveryAddress.pincode` must be 6 digits

**Server-side logic:**
1. Validate restaurant exists
2. Validate each menu item exists and belongs to the restaurant
3. Look up current price for each item from menu_items table
4. Calculate total = sum of (price × quantity)
5. Save order with status `Created`

**Response: 201 Created**

```json
{
  "id": "d1b2c3d4-0001-4000-8000-000000000001",
  "customerId": "c1b2c3d4-0001-4000-8000-000000000001",
  "restaurantId": "a1b2c3d4-0001-4000-8000-000000000001",
  "status": "Created",
  "items": [
    {
      "id": "e1b2c3d4-0001-4000-8000-000000000001",
      "menuItemId": "b1b2c3d4-0001-4000-8000-000000000001",
      "name": "Chicken Biryani",
      "quantity": 2,
      "unitPrice": {
        "amount": 299.00,
        "currency": "INR"
      }
    },
    {
      "id": "e1b2c3d4-0002-4000-8000-000000000002",
      "menuItemId": "b1b2c3d4-0003-4000-8000-000000000003",
      "name": "Paneer Butter Masala",
      "quantity": 1,
      "unitPrice": {
        "amount": 249.00,
        "currency": "INR"
      }
    }
  ],
  "totalAmount": {
    "amount": 847.00,
    "currency": "INR"
  },
  "deliveryAddress": {
    "line1": "302, Prestige Lakeside Habitat",
    "line2": "Whitefield",
    "city": "Bangalore",
    "pincode": "560066"
  },
  "createdAt": "2025-01-15T13:30:00Z"
}
```

**Response: 400 Bad Request** — Validation error  
**Response: 404 Not Found** — Restaurant or menu item not found

---

### GET /api/v1/orders/{orderId}

Get order details by ID.

**Response: 200 OK** — Same shape as POST response above

**Response: 404 Not Found**

---

### GET /api/v1/orders

List orders with optional customer filter and pagination.

**Query Parameters:**

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| customerId | Guid | — | Filter by customer |
| page | int | 1 | Page number |
| pageSize | int | 10 | Items per page (max 50) |

**Response: 200 OK**

```json
{
  "items": [
    {
      "id": "d1b2c3d4-0001-4000-8000-000000000001",
      "customerId": "c1b2c3d4-0001-4000-8000-000000000001",
      "restaurantId": "a1b2c3d4-0001-4000-8000-000000000001",
      "status": "Created",
      "totalAmount": {
        "amount": 847.00,
        "currency": "INR"
      },
      "createdAt": "2025-01-15T13:30:00Z"
    }
  ],
  "page": 1,
  "pageSize": 10,
  "totalCount": 1,
  "totalPages": 1
}
```

---

### PATCH /api/v1/orders/{orderId}/status

Update order status. Enforces a state machine.

**Valid Transitions:**

```
Created → Confirmed → Preparing → ReadyForPickup → PickedUp → Delivered
Created → Cancelled
Confirmed → Cancelled
```

Invalid transitions return `422 Unprocessable Entity`.

**Request Body:**

```json
{
  "status": "Confirmed"
}
```

**Response: 204 No Content**

**Response: 404 Not Found** — Order not found  
**Response: 422 Unprocessable Entity** — Invalid state transition

---

### POST /api/v1/orders/{orderId}/cancel

Cancel an order. Only possible when status is Created or Confirmed.

**Request Body:**

```json
{
  "reason": "Changed my mind"
}
```

**Response: 204 No Content**

**Response: 404 Not Found** — Order not found  
**Response: 422 Unprocessable Entity** — Order is past the cancellable window

---

## Future Endpoints (Not implemented in Day 3)

These are defined in the contract for completeness. They'll be built in later days.

### Delivery Endpoints (Day 4+)
- `POST /api/v1/deliveries/assign` — Assign delivery agent to order
- `GET /api/v1/deliveries/{deliveryId}` — Get delivery status
- `PATCH /api/v1/deliveries/{deliveryId}/status` — Update delivery status
- `GET /api/v1/deliveries/{deliveryId}/location` — Get agent location

### User Endpoints (Day 6+)
- `POST /api/v1/users/register` — Register
- `POST /api/v1/users/login` — Login (returns JWT)
- `GET /api/v1/users/{userId}/profile` — Get profile
- `PUT /api/v1/users/{userId}/addresses` — Update saved addresses

### Payment Endpoints (Day 7+)
- `POST /api/v1/payments` — Initiate payment
- `GET /api/v1/payments/{paymentId}` — Get payment status
- `POST /api/v1/payments/{paymentId}/refund` — Initiate refund

---

## Common Patterns

### Pagination

All list endpoints use offset-based pagination with `page` and `pageSize` query parameters. Response wraps items in a `PagedResponse<T>`:

```json
{
  "items": [],
  "page": 1,
  "pageSize": 10,
  "totalCount": 42,
  "totalPages": 5
}
```

### Money Fields

All monetary values use the Money value object pattern:

```json
{
  "amount": 299.00,
  "currency": "INR"
}
```

Never a naked decimal. The currency field prevents bugs when you expand to multiple currencies.

### Date Format

ISO 8601 with UTC: `"2025-01-15T13:30:00Z"`

Stored as `TIMESTAMPTZ` in PostgreSQL. Frontend converts to IST for display.
