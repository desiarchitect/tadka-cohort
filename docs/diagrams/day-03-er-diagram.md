# Day 3: Database ER Diagram

## Entity Relationship Diagram

All tables in one PostgreSQL database, separated by schema. No cross-schema foreign keys.

```mermaid
erDiagram
    %% ===== RESTAURANT SCHEMA (green) =====
    restaurants {
        uuid id PK
        varchar name
        varchar address_line1
        varchar address_line2
        varchar address_city
        varchar address_pincode
        decimal address_latitude
        decimal address_longitude
        boolean is_active
        int avg_prep_time_minutes
        timestamptz created_at
        timestamptz updated_at
    }

    menu_items {
        uuid id PK
        uuid restaurant_id FK
        varchar name
        varchar description
        decimal price_amount
        varchar price_currency
        varchar category
        boolean is_available
        boolean is_veg
        timestamptz created_at
        timestamptz updated_at
    }

    restaurants ||--o{ menu_items : "has"

    %% ===== ORDERING SCHEMA (blue) =====
    orders {
        uuid id PK
        uuid customer_id
        varchar customer_name
        varchar customer_phone
        uuid restaurant_id
        varchar restaurant_name
        varchar status
        decimal total_amount
        varchar total_currency
        varchar delivery_line1
        varchar delivery_line2
        varchar delivery_city
        varchar delivery_pincode
        decimal delivery_latitude
        decimal delivery_longitude
        varchar cancellation_reason
        timestamptz created_at
        timestamptz updated_at
    }

    order_status_history {
        uuid id PK
        uuid order_id FK
        varchar from_status
        varchar to_status
        varchar actor
        uuid actor_id
        varchar notes
        timestamptz created_at
    }

    orders ||--o{ order_status_history : "tracks"

    order_items {
        uuid id PK
        uuid order_id FK
        uuid menu_item_id
        varchar name
        int quantity
        decimal unit_price_amount
        varchar unit_price_currency
        varchar special_instructions
    }

    orders ||--o{ order_items : "contains"

    %% ===== IDENTITY SCHEMA =====
    users {
        uuid id PK
        varchar email
        varchar password_hash
        varchar name
        varchar phone
        varchar role
        timestamptz created_at
    }

    user_addresses {
        uuid id PK
        uuid user_id FK
        varchar label
        varchar line1
        varchar line2
        varchar city
        varchar pincode
        decimal latitude
        decimal longitude
        boolean is_default
    }

    users ||--o{ user_addresses : "has"

    %% ===== DELIVERY SCHEMA =====
    delivery_agents {
        uuid id PK
        varchar name
        varchar phone
        varchar status
        decimal current_latitude
        decimal current_longitude
        boolean is_available
    }

    deliveries {
        uuid id PK
        uuid order_id
        uuid agent_id
        varchar status
        decimal pickup_latitude
        decimal pickup_longitude
        decimal dropoff_latitude
        decimal dropoff_longitude
        timestamptz picked_up_at
        timestamptz delivered_at
    }

    delivery_agents ||--o{ deliveries : "assigned"

    %% ===== PAYMENT SCHEMA =====
    payments {
        uuid id PK
        uuid order_id
        decimal amount
        varchar currency
        varchar method
        varchar status
        varchar gateway_txn_id
        jsonb gateway_response
        timestamptz created_at
    }
```

## Schema Ownership

| Schema | Tables | Status |
|--------|--------|--------|
| `restaurant` | restaurants, menu_items | ✅ Implemented (Day 2-3) |
| `ordering` | orders, order_items | ✅ Implemented (Day 2-3) |
| `identity` | users, user_addresses | 🔲 Planned (Day 6) |
| `delivery` | delivery_agents, deliveries | 🔲 Planned (Day 4) |
| `payment` | payments | 🔲 Planned (Day 7) |

## Design Rules

1. **No cross-schema foreign keys.** `orders.restaurant_id` is NOT a FK to `restaurant.restaurants`. It's a plain UUID. This makes future service extraction possible.

2. **Denormalized order_items.** `order_items.name` and `unit_price_amount` are copied from menu_items at order time. If the restaurant changes the price later, existing orders aren't affected.

3. **VARCHAR for status fields.** Not enums. Adding a new status doesn't require a migration.

4. **Value objects stored as owned entities.** `Address`, `Money` are flattened into parent table columns via EF Core's `OwnsOne`.
