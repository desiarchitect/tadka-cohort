# Day 3: Database ER Diagram

## Entity Relationship Diagram

All tables in one PostgreSQL database, separated by schema. **No cross-schema foreign keys** — cross-domain links are plain UUIDs. (Mirrors the live EF model; column casing is mixed because owned value objects use snake_case.)

```mermaid
erDiagram
    %% ===== restaurant schema =====
    restaurants {
        uuid Id PK
        varchar Name
        varchar address_line1
        varchar address_line2
        varchar address_city
        varchar address_pincode
        double latitude
        double longitude
        boolean IsActive
        int AvgPrepTimeMinutes
        timestamptz CreatedAt
    }
    menu_items {
        uuid Id PK
        uuid RestaurantId FK
        varchar Name
        varchar Description
        numeric price
        varchar currency
        varchar Category
        boolean IsAvailable
        boolean IsVeg
    }
    restaurants ||--o{ menu_items : "has"

    %% ===== ordering schema =====
    orders {
        uuid Id PK
        uuid CustomerId
        uuid RestaurantId
        varchar Status
        numeric total_amount
        varchar currency
        varchar delivery_address_line1
        varchar delivery_address_city
        varchar delivery_address_pincode
        double delivery_latitude
        double delivery_longitude
        timestamptz CreatedAt
        timestamptz ConfirmedAt
        timestamptz DeliveredAt
        timestamptz CancelledAt
        varchar CancellationReason
    }
    order_items {
        uuid Id PK
        uuid OrderId FK
        uuid MenuItemId
        varchar Name
        int Quantity
        numeric unit_price
        varchar currency
        varchar SpecialInstructions
    }
    orders ||--o{ order_items : "contains"

    %% ===== identity schema =====
    users {
        uuid Id PK
        varchar Name
        varchar Email
        varchar Phone
        varchar PasswordHash
        varchar Role
        timestamptz CreatedAt
    }
    user_addresses {
        uuid Id PK
        uuid UserId FK
        varchar Label
        varchar line1
        varchar city
        varchar pincode
        double latitude
        double longitude
        boolean IsDefault
    }
    users ||--o{ user_addresses : "has"

    %% ===== delivery schema =====
    agents {
        uuid Id PK
        varchar Name
        varchar Phone
        varchar Status
        double current_latitude
        double current_longitude
    }
    assignments {
        uuid Id PK
        uuid OrderId
        uuid AgentId FK
        varchar Status
        timestamptz AssignedAt
        timestamptz PickedUpAt
        timestamptz DeliveredAt
    }
    agents ||--o{ assignments : "assigned"

    %% ===== payment schema =====
    payments {
        uuid Id PK
        uuid OrderId
        numeric amount
        varchar currency
        varchar Method
        varchar Status
        varchar GatewayReference
        timestamptz CreatedAt
        timestamptz CompletedAt
    }
```

## Schema Ownership

| Schema | Tables | Status |
|--------|--------|--------|
| `restaurant` | restaurants, menu_items | ✅ live + seeded |
| `ordering` | orders, order_items | ✅ live |
| `identity` | users, user_addresses | ✅ live (1 customer seeded; auth wired later) |
| `delivery` | agents, assignments | ✅ schema present (delivery endpoints come Day 4+) |
| `payment` | payments | ✅ schema present (processing comes Day 7) |

## Design Rules

1. **No cross-schema foreign keys** (ADR-008). `orders.RestaurantId`, `order_items.MenuItemId`, `assignments.OrderId`, `payments.OrderId` are plain UUIDs, validated in the application layer. This keeps each schema independently extractable.
2. **Within-schema FKs only**: `menu_items → restaurants`, `order_items → orders`, `user_addresses → users`, `assignments → agents`.
3. **Denormalized snapshots** (ADR-009): `order_items.Name` + `unit_price` are copied from the menu at order time — historical accuracy.
4. **VARCHAR status, not ENUM** — flexible to add new states without an `ALTER TYPE` migration.
5. **Value objects via `OwnsOne`** — `Money`, `Address`, `GeoLocation` flatten into parent columns (the snake_case ones).
6. **No hard DELETE** — `IsActive` / `IsAvailable` / `Cancelled` instead.
