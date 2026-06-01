# API Design Guide

Standards and conventions for all Tadka API endpoints. Follow these when building new endpoints in future days.

> **Contract-first.** The API contract ([`api-contracts.md`](api-contracts.md)) is designed and agreed *before* controllers are written — the code implements the contract. All routes live under **`/api/v1`** (ADR-010). The contract is the product surface today and the **service boundary** at extraction (Week 4+), so treat every shape, status code, and error as a promise to a consumer you can't break casually.

---

## 1. Resource Naming

**Use nouns, not verbs.** The HTTP method already tells you the action.

```
✅ GET  /api/restaurants
✅ POST /api/orders
✅ GET  /api/orders/{orderId}

❌ GET  /api/getRestaurants
❌ POST /api/createOrder
❌ GET  /api/fetchOrderById
```

**Use plural for collections.** Even if you're fetching one item, the resource is part of a collection.

```
✅ /api/restaurants/{id}
❌ /api/restaurant/{id}
```

**Use kebab-case for multi-word resources.**

```
✅ /api/menu-items
❌ /api/menuItems
❌ /api/menu_items
```

**Nest sub-resources under their parent.** Menu items belong to a restaurant.

```
✅ GET /api/restaurants/{restaurantId}/menu
❌ GET /api/menu-items?restaurantId={id}
```

---

## 2. HTTP Method Semantics

| Method | Purpose | Idempotent | Request Body |
|--------|---------|------------|--------------|
| GET | Read resource(s) | Yes | No |
| POST | Create resource / trigger action | No | Yes |
| PUT | Full replace | Yes | Yes |
| PATCH | Partial update | Yes | Yes |
| DELETE | Remove resource | Yes | No |

**POST is NOT idempotent.** Calling `POST /api/orders` twice creates two orders. Design accordingly.

**PATCH vs PUT:**
- PATCH updates specific fields (we use this for status updates, availability toggles)
- PUT replaces the entire resource (not used in Day 3)

---

## 3. Status Code Quick Reference

### Success Codes

| Code | When to use |
|------|-------------|
| 200 OK | GET requests, successful reads |
| 201 Created | POST that creates a resource. Include `Location` header. |
| 204 No Content | PATCH/DELETE that succeeds with no response body |

### Client Error Codes

| Code | When to use |
|------|-------------|
| 400 Bad Request | Validation errors (malformed input) |
| 401 Unauthorized | Missing or invalid auth token (Day 6+) |
| 403 Forbidden | Valid auth but insufficient permissions (Day 6+) |
| 404 Not Found | Resource doesn't exist |
| 422 Unprocessable Entity | Valid syntax but violates business rules |

### Server Error Codes

| Code | When to use |
|------|-------------|
| 500 Internal Server Error | Unhandled exception (never expose details) |

**400 vs 422:** If the JSON is malformed or fails schema validation, it's 400. If the JSON is valid but the operation violates a domain rule (like cancelling a delivered order), it's 422.

---

## 4. Error Responses

All errors use RFC 7807 Problem Details format. See [error-standard.md](api-specs/error-standard.md) for full specification and examples.

---

## 5. Pagination

All list endpoints use offset-based pagination.

**Query Parameters:**

| Param | Type | Default | Max |
|-------|------|---------|-----|
| page | int | 1 | - |
| pageSize | int | 10 | 50 |

**Response Wrapper:**

```json
{
  "items": [...],
  "page": 1,
  "pageSize": 10,
  "totalCount": 42,
  "totalPages": 5
}
```

**Why offset-based (not cursor-based)?**

Offset pagination is simpler to implement and sufficient for our scale. Cursor-based pagination is better for infinite scrolling and large datasets where rows might be inserted/deleted between page fetches. We'll switch to cursor-based if needed.

---

## 6. Filtering and Sorting

Filters go as query parameters:

```
GET /api/restaurants?city=Bangalore
GET /api/restaurants/{id}/menu?vegOnly=true&category=Main+Course
GET /api/orders?customerId={guid}
```

Sorting (not implemented yet, design for future):

```
GET /api/restaurants?sortBy=name&sortOrder=asc
```

---

## 7. Content Type

All requests and responses use `application/json`.

Error responses use `application/problem+json` (RFC 7807).

Always set `Content-Type: application/json` in POST/PUT/PATCH requests.

---

## 8. Date and Time Format

**ISO 8601 with UTC timezone.**

```json
{
  "createdAt": "2025-01-15T13:30:00Z"
}
```

- Always store as `TIMESTAMPTZ` in PostgreSQL
- Always serialize with `Z` suffix (UTC)
- Frontend converts to local timezone (IST) for display
- Never send timestamps without timezone information

---

## 9. Money Fields

Always use a structured Money object. Never a naked decimal.

```json
{
  "amount": 299.00,
  "currency": "INR"
}
```

This prevents currency ambiguity when you eventually support multiple currencies or integrate with payment gateways that need the currency code.

---

## 10. ID Format

Use UUID v4 for all resource identifiers.

```json
{
  "id": "a1b2c3d4-0001-4000-8000-000000000001"
}
```

- Generated server-side (never by the client)
- Stored as `UUID` type in PostgreSQL
- Exposed as lowercase hyphenated string in JSON

**Why UUIDs over auto-increment?**

Auto-increment IDs leak information (competitor can estimate your order volume by placing two orders an hour apart). UUIDs are unguessable and don't expose business metrics.

---

## 11. Versioning Strategy (Decided — ADR-010)

**All routes ship under `/api/v1` from day one.** This is a decision, not a "later" — you version *before* the first client depends on you, not after the first breaking change. See [ADR-010](adrs/010-api-versioning.md).

- **URL-based versioning:** `/api/v1/restaurants`, and a future `/api/v2/restaurants` for breaking changes.
- **Additive changes** (new endpoint, new optional field) → **no version bump.** Clients ignore unknown fields.
- **Breaking changes** (remove/rename a field, change a type or semantics, remove an endpoint) → **new major** (`/v2`).
- A retired major (`v1`) stays alive **~6 months** after its successor is GA, serving `Deprecation`/`Sunset` headers.

---

## 12. Naming Conventions

| Context | Convention | Example |
|---------|-----------|---------|
| URL paths | kebab-case | `/api/menu-items` |
| Query params | camelCase | `?pageSize=10&vegOnly=true` |
| JSON fields | camelCase | `"totalAmount"`, `"isActive"` |
| HTTP headers | Title-Case | `Content-Type`, `Authorization` |

ASP.NET Core's default JSON serializer (System.Text.Json) converts PascalCase C# properties to camelCase automatically.
