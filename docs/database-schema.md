# Tadka Database Schema

One PostgreSQL database, five schemas. Each schema maps to a domain boundary.

When we extract services later, each service takes its schema. The migration path: schema → separate database → separate service.

---

## Schema Overview

| Schema | Tables | Purpose |
|--------|--------|---------|
| `ordering` | orders, order_items | Order lifecycle |
| `restaurant` | restaurants, menu_items | Restaurant catalog and menus |
| `delivery` | delivery_agents, delivery_assignments | Delivery agent management |
| `identity` | users, user_addresses | User accounts and saved addresses |
| `payment` | payments | Payment processing |

**Total: 9 tables across 5 schemas.**

---

## Schema: ordering

### orders

| Column | Type | Constraints | Notes |
|--------|------|-------------|-------|
| id | UUID | PK | |
| customer_id | UUID | NOT NULL | No FK to identity.users (cross-schema) |
| customer_name | VARCHAR | nullable | Snapshot at order time |
| customer_phone | VARCHAR | nullable | Snapshot at order time |
| restaurant_id | UUID | NOT NULL | No FK to restaurant.restaurants (cross-schema) |
| restaurant_name | VARCHAR | nullable | Snapshot at order time |
| status | VARCHAR | NOT NULL | Created, Confirmed, Preparing, ReadyForPickup, PickedUp, Delivered, Cancelled, Refunded |
| total_amount_amount | DECIMAL(10,2) | NOT NULL | Money value object (OwnsOne) |
| total_amount_currency | VARCHAR | NOT NULL, default 'INR' | |
| delivery_address_line1 | VARCHAR | | Address value object (OwnsOne) |
| delivery_address_line2 | VARCHAR | | |
| delivery_address_city | VARCHAR | | |
| delivery_address_pincode | VARCHAR | | |
| delivery_address_latitude | DOUBLE | | |
| delivery_address_longitude | DOUBLE | | |
| created_at | TIMESTAMPTZ | NOT NULL | |
| confirmed_at | TIMESTAMPTZ | nullable | Set when status → Confirmed |
| delivered_at | TIMESTAMPTZ | nullable | Set when status → Delivered |
| cancelled_at | TIMESTAMPTZ | nullable | Set when status → Cancelled |
| cancellation_reason | VARCHAR | nullable | |

### order_status_history

| Column | Type | Constraints | Notes |
|--------|------|-------------|-------|
| id | UUID | PK | |
| order_id | UUID | FK → orders(id), CASCADE | Within-schema FK |
| from_status | VARCHAR | nullable | |
| to_status | VARCHAR | NOT NULL | |
| actor | VARCHAR | NOT NULL | System, Customer, Restaurant, DeliveryAgent |
| actor_id | UUID | nullable | |
| notes | VARCHAR | nullable | |
| created_at | TIMESTAMPTZ | NOT NULL | |

### order_items

| Column | Type | Constraints | Notes |
|--------|------|-------------|-------|
| id | UUID | PK | |
| order_id | UUID | FK → orders(id), CASCADE | Within-schema FK is fine |
| menu_item_id | UUID | NOT NULL | No FK to restaurant.menu_items |
| name | VARCHAR | NOT NULL | Denormalized: snapshot at order time |
| quantity | INT | NOT NULL | |
| unit_price_amount | DECIMAL(10,2) | NOT NULL | Snapshot at order time |
| unit_price_currency | VARCHAR | NOT NULL, default 'INR' | |
| special_instructions | VARCHAR | nullable | |

---

## Schema: restaurant

### restaurants

| Column | Type | Constraints | Notes |
|--------|------|-------------|-------|
| id | UUID | PK | |
| name | VARCHAR | NOT NULL | |
| address_line1 | VARCHAR | | Address value object (OwnsOne) |
| address_line2 | VARCHAR | | |
| address_city | VARCHAR | | |
| address_pincode | VARCHAR | | |
| address_latitude | DOUBLE | | |
| address_longitude | DOUBLE | | |
| is_active | BOOLEAN | NOT NULL | |
| avg_prep_time_minutes | INT | NOT NULL, default 30 | |
| created_at | TIMESTAMPTZ | NOT NULL | |

### menu_items

| Column | Type | Constraints | Notes |
|--------|------|-------------|-------|
| id | UUID | PK | |
| restaurant_id | UUID | FK → restaurants(id), CASCADE | Within-schema FK |
| name | VARCHAR | NOT NULL | |
| description | VARCHAR | nullable | |
| price_amount | DECIMAL(10,2) | NOT NULL | Money value object (OwnsOne) |
| price_currency | VARCHAR | NOT NULL, default 'INR' | |
| category | VARCHAR | NOT NULL | Main Course, Starters, Rice, etc. |
| is_available | BOOLEAN | NOT NULL | |
| is_veg | BOOLEAN | NOT NULL | |

---

## Schema: delivery

### delivery_agents

| Column | Type | Constraints | Notes |
|--------|------|-------------|-------|
| id | UUID | PK | |
| name | VARCHAR | NOT NULL | |
| phone | VARCHAR | NOT NULL | |
| status | VARCHAR | NOT NULL | Available, OnDelivery, Offline |
| current_location_latitude | DOUBLE | | GeoLocation value object (OwnsOne) |
| current_location_longitude | DOUBLE | | |

### delivery_assignments

| Column | Type | Constraints | Notes |
|--------|------|-------------|-------|
| id | UUID | PK | |
| order_id | UUID | NOT NULL | No FK to ordering.orders |
| agent_id | UUID | FK → delivery_agents(id), RESTRICT | Within-schema FK |
| status | VARCHAR | NOT NULL | Assigned, PickedUp, Delivered, Cancelled |
| assigned_at | TIMESTAMPTZ | NOT NULL | |
| picked_up_at | TIMESTAMPTZ | nullable | |
| delivered_at | TIMESTAMPTZ | nullable | |

---

## Schema: identity

### users

| Column | Type | Constraints | Notes |
|--------|------|-------------|-------|
| id | UUID | PK | |
| name | VARCHAR | NOT NULL | |
| email | VARCHAR | NOT NULL, UNIQUE INDEX | |
| phone | VARCHAR | nullable | |
| password_hash | VARCHAR | NOT NULL | |
| role | VARCHAR | NOT NULL | Customer, RestaurantOwner, DeliveryAgent, Admin |
| created_at | TIMESTAMPTZ | NOT NULL | |

### user_addresses

| Column | Type | Constraints | Notes |
|--------|------|-------------|-------|
| id | UUID | PK | |
| user_id | UUID | FK → users(id), CASCADE | Within-schema FK |
| label | VARCHAR | nullable | "Home", "Office", etc. |
| address_line1 | VARCHAR | | Address value object (OwnsOne) |
| address_line2 | VARCHAR | | |
| address_city | VARCHAR | | |
| address_pincode | VARCHAR | | |
| address_latitude | DOUBLE | | |
| address_longitude | DOUBLE | | |
| is_default | BOOLEAN | NOT NULL | |

---

## Schema: payment

### payments

| Column | Type | Constraints | Notes |
|--------|------|-------------|-------|
| id | UUID | PK | |
| order_id | UUID | NOT NULL | No FK to ordering.orders |
| amount_amount | DECIMAL(10,2) | NOT NULL | Money value object (OwnsOne) |
| amount_currency | VARCHAR | NOT NULL, default 'INR' | |
| method | VARCHAR | NOT NULL | |
| status | VARCHAR | NOT NULL | Pending, Completed, Failed, Refunded |
| gateway_reference | VARCHAR | nullable | |
| gateway_response | JSONB | nullable | Raw provider response |
| created_at | TIMESTAMPTZ | NOT NULL | |
| completed_at | TIMESTAMPTZ | nullable | |

---

## Key Design Rules

### No cross-schema foreign keys

`ordering.orders.customer_id` has NO FK to `identity.users.id`. This is intentional. When we extract services, each service owns its schema. Cross-schema FKs would create an unmovable dependency.

### FKs within schema are fine

`order_items → orders`, `menu_items → restaurants`, `user_addresses → users`, `delivery_assignments → delivery_agents`. These entities always live together.

### Denormalized data where needed

`order_items.name` and `order_items.unit_price` are snapshots captured at order creation time. If the restaurant renames "Chicken Biryani" to "Hyderabadi Dum Biryani" next week, existing orders still show what the customer originally ordered.

### VARCHAR for status fields, not PostgreSQL ENUM

PostgreSQL ENUMs are immutable once created. Adding a new status like `PartiallyRefunded` requires `ALTER TYPE... ADD VALUE` which can't run inside a transaction. VARCHAR with C# enum validation is more flexible.

### Value objects via EF Core OwnsOne

`Money(Amount, Currency)`, `Address(Line1, Line2, City, Pincode, Latitude, Longitude)`, and `GeoLocation(Latitude, Longitude)` are mapped as owned types. No separate tables, no JOINs. The value object columns live on the parent table.

---

## Seed Data

3 restaurants seeded via EF Core `HasData()`:

| Restaurant | Location | Prep Time | Menu Items |
|-----------|----------|-----------|------------|
| Meghana Foods | Koramangala, Bangalore | 25 min | 6 items (Biryani, South Indian) |
| Truffles | Indiranagar, Bangalore | 30 min | 5 items (Burgers, Continental) |
| Vidyarthi Bhavan | Basavanagudi, Bangalore | 20 min | 5 items (South Indian, Breakfast) |
