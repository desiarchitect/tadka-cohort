# Day 3: API Route Map

## All Implemented Endpoints

```mermaid
graph LR
    subgraph "Tadka API (localhost:5224)"
        subgraph "Health"
            H1["GET /health"]
            H2["GET /health/ready"]
        end

        subgraph "Restaurant Domain"
            R1["GET /api/v1/restaurants"]
            R2["GET /api/v1/restaurants/{id}"]
            R3["POST /api/v1/restaurants"]
            R4["PATCH /api/v1/restaurants/{id}"]
            R5["GET /api/v1/restaurants/{id}/menu"]
            R6["POST /api/v1/restaurants/{id}/menu"]
            R7["PATCH /api/v1/restaurants/{id}/menu/{itemId}"]
            R8["PATCH /api/v1/restaurants/{id}/menu/{itemId}/availability"]
        end

        subgraph "Order Domain"
            O1["POST /api/v1/orders"]
            O2["GET /api/v1/orders/{id}"]
            O3["GET /api/v1/orders"]
            O4["PATCH /api/v1/orders/{id}/status"]
            O5["POST /api/v1/orders/{id}/cancel"]
        end
    end
```

## Endpoint Summary Table

### Restaurant Endpoints (8)

| Method | Route | Purpose | Status Codes |
|--------|-------|---------|-------------|
| GET | `/api/v1/restaurants` | List with city filter, pagination | 200 |
| GET | `/api/v1/restaurants/{id}` | Get single restaurant | 200, 404 |
| POST | `/api/v1/restaurants` | Create restaurant | 201, 400 |
| PATCH | `/api/v1/restaurants/{id}` | Update / deactivate restaurant (no DELETE) | 204, 404 |
| GET | `/api/v1/restaurants/{id}/menu` | List menu items (vegOnly, category filter) | 200, 404 |
| POST | `/api/v1/restaurants/{id}/menu` | Add a menu item | 201, 400, 404 |
| PATCH | `/api/v1/restaurants/{id}/menu/{itemId}` | Edit item (price, desc, availability) | 204, 404 |
| PATCH | `/api/v1/restaurants/{id}/menu/{itemId}/availability` | Quick availability toggle | 204, 404 |

### Order Endpoints (5)

| Method | Route | Purpose | Status Codes |
|--------|-------|---------|-------------|
| POST | `/api/v1/orders` | Create order (server-side pricing) | 201, 400, 404 |
| GET | `/api/v1/orders/{id}` | Get order details | 200, 404 |
| GET | `/api/v1/orders` | List with customer filter, pagination | 200 |
| PATCH | `/api/v1/orders/{id}/status` | Update status (state machine) | 204, 404, 422 |
| POST | `/api/v1/orders/{id}/cancel` | Cancel order | 204, 404, 422 |

### Health (2)

| Method | Route | Purpose | Status Codes |
|--------|-------|---------|-------------|
| GET | `/health` | Liveness check | 200 |
| GET | `/health/ready` | Readiness check (DB) | 200/503 |

**Total: 15 endpoints** (8 restaurant + 5 order + 2 health (liveness + readiness)). No `PUT`, no `DELETE` — all updates are partial (`PATCH`); removal is deactivate/soft-hide/cancel.

## Order State Machine

```mermaid
stateDiagram-v2
    [*] --> Created
    Created --> Confirmed
    Created --> Cancelled
    Confirmed --> Preparing
    Confirmed --> Cancelled
    Preparing --> ReadyForPickup
    ReadyForPickup --> PickedUp
    PickedUp --> Delivered
    Delivered --> [*]
    Cancelled --> [*]
```

## Future Endpoints (Not Implemented)

| Domain | Endpoints | Planned |
|--------|-----------|---------|
| Delivery | 4 endpoints | Day 4 |
| User/Auth | 4 endpoints | Day 6 |
| Payment | 3 endpoints | Day 7 |
