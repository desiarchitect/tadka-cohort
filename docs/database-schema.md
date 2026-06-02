# Tadka Database Schema

One PostgreSQL database, five schemas. Each schema maps to a domain boundary. When we extract services later, each service takes its schema: schema → separate database → separate service.

> **This doc follows the EF model** (`Data/Configurations/*`), regenerated from the live database. If it disagrees with the code, the code wins — fix the doc.
>
> **Naming note:** column casing is mixed — scalar properties keep EF's PascalCase (`Id`, `Status`, `CreatedAt`), while **value objects mapped via `OwnsOne`** use explicit snake_case (`total_amount`, `delivery_address_*`). A known minor inconsistency; the boundaries and types are what matter for teaching.

---

## Schema Overview

| Schema | Tables | Purpose |
|--------|--------|---------|
| `ordering` | orders, order_items | Order lifecycle |
| `restaurant` | restaurants, menu_items | Restaurant catalog and menus |
| `delivery` | agents, assignments | Delivery agent management |
| `identity` | users, user_addresses | User accounts and saved addresses |
| `payment` | payments | Payment records (no processing yet — Day 7) |

**Total: 9 tables across 5 schemas.**

---

## Schema: ordering

### orders
| Column | Type | Notes |
|--------|------|-------|
| Id | uuid PK | `gen_random_uuid()` default |
| CustomerId | uuid | No FK to `identity.users` (cross-schema) |
| RestaurantId | uuid | No FK to `restaurant.restaurants` (cross-schema) |
| Status | varchar(20) | Created, Confirmed, Preparing, ReadyForPickup, PickedUp, Delivered, Cancelled, Refunded |
| total_amount | numeric(10,2) | Money value object (`OwnsOne`) |
| currency | varchar(3) | default `INR` |
| delivery_address_line1/line2/city/pincode | varchar | Address value object (`OwnsOne`) |
| delivery_latitude / delivery_longitude | double | |
| CreatedAt | timestamptz | |
| ConfirmedAt / DeliveredAt / CancelledAt | timestamptz nullable | set on transition |
| CancellationReason | varchar nullable | |

### order_items
| Column | Type | Notes |
|--------|------|-------|
| Id | uuid PK | |
| OrderId | uuid | FK → `ordering.orders(Id)`, CASCADE (within-schema FK is fine) |
| MenuItemId | uuid | No FK to `restaurant.menu_items` |
| Name | varchar | **Snapshot** at order time |
| Quantity | int | |
| unit_price | numeric(10,2) | **Snapshot** Money (`OwnsOne`) |
| currency | varchar(3) | |
| SpecialInstructions | varchar nullable | |

---

## Schema: restaurant

### restaurants
| Column | Type | Notes |
|--------|------|-------|
| Id | uuid PK | |
| Name | varchar | |
| address_line1/line2/city/pincode | varchar | Address value object (`OwnsOne`) |
| latitude / longitude | double | |
| IsActive | boolean | deactivate = our "delete" (no hard DELETE) |
| AvgPrepTimeMinutes | int | default 30 |
| CreatedAt | timestamptz | |

### menu_items
| Column | Type | Notes |
|--------|------|-------|
| Id | uuid PK | |
| RestaurantId | uuid | FK → `restaurant.restaurants(Id)`, CASCADE (within-schema) |
| Name | varchar | |
| Description | varchar nullable | |
| price | numeric(10,2) | Money value object (`OwnsOne`) |
| currency | varchar(3) | |
| Category | varchar | |
| IsAvailable | boolean | "unavailable" = soft-hide (no hard DELETE) |
| IsVeg | boolean | |

---

## Schema: delivery

### agents
| Column | Type | Notes |
|--------|------|-------|
| Id | uuid PK | |
| Name / Phone | varchar | |
| Status | varchar(20) | Offline, Available, OnDelivery |
| current_latitude / current_longitude | double | GeoLocation value object (`OwnsOne`) |

### assignments
| Column | Type | Notes |
|--------|------|-------|
| Id | uuid PK | |
| OrderId | uuid | No FK to `ordering.orders` (cross-schema) |
| AgentId | uuid | FK → `delivery.agents(Id)` (within-schema) |
| Status | varchar(20) | Assigned, PickedUp, Delivered, Cancelled |
| AssignedAt | timestamptz | |
| PickedUpAt / DeliveredAt | timestamptz nullable | |

---

## Schema: identity

### users
| Column | Type | Notes |
|--------|------|-------|
| Id | uuid PK | |
| Name | varchar | |
| Email | varchar | UNIQUE index |
| Phone | varchar | |
| PasswordHash | varchar | (auth wired later) |
| Role | varchar(20) | Customer, RestaurantOwner, DeliveryAgent, Admin |
| CreatedAt | timestamptz | |

### user_addresses
| Column | Type | Notes |
|--------|------|-------|
| Id | uuid PK | |
| UserId | uuid | FK → `identity.users(Id)`, CASCADE (within-schema) |
| Label | varchar nullable | |
| line1/line2/city/pincode | varchar | Address value object (`OwnsOne`) |
| latitude / longitude | double | |
| IsDefault | boolean | |

---

## Schema: payment

### payments
| Column | Type | Notes |
|--------|------|-------|
| Id | uuid PK | |
| OrderId | uuid | No FK to `ordering.orders` |
| amount | numeric(10,2) | Money value object (`OwnsOne`) |
| currency | varchar(3) | |
| Method | varchar | |
| Status | varchar(20) | Pending, Completed, Failed, Refunded |
| GatewayReference | varchar nullable | |
| CreatedAt | timestamptz | |
| CompletedAt | timestamptz nullable | |

> The `payments` table exists (schema-per-domain shows every domain), but **payment processing is not wired until Day 7** — there is no payment endpoint or service on this branch.

---

## Key Design Rules

### No cross-schema foreign keys (ADR-008)
`ordering.orders.CustomerId` has NO FK to `identity.users`. `order_items.MenuItemId` has NO FK to `restaurant.menu_items`. Cross-schema references are plain UUIDs validated in the application layer. This is what makes service extraction (Week 4+) a clean schema move, not a query rewrite.

### FKs within a schema are fine
`order_items → orders`, `menu_items → restaurants`, `user_addresses → users`, `assignments → agents`. These entities always live together.

### Denormalized snapshots (ADR-009)
`order_items.Name` and `unit_price` are captured at order time. If a restaurant renames a dish or changes the price tomorrow, existing orders are unchanged — historical accuracy, not duplication.

### VARCHAR for status, not PostgreSQL ENUM
ENUMs are immutable once created; adding a status (`PartiallyRefunded`) needs `ALTER TYPE … ADD VALUE` which can't run in a transaction. VARCHAR + C# enum validation (`HasConversion<string>()`) is more flexible.

### Value objects via EF Core `OwnsOne`
`Money(Amount, Currency)`, `Address(...)`, `GeoLocation(...)` are owned types — no separate tables; columns live on the parent row.

### Removal is a state change, never a hard DELETE
Restaurant → `IsActive=false`; menu item → `IsAvailable=false`; order → `Cancelled`. A system with order history never loses rows.

---

## Seed Data (migrations)

3 restaurants + 16 menu items + 1 customer, via EF `HasData`:

| Restaurant | GUID | Items |
|-----------|------|-------|
| Meghana Foods | `a1b2c3d4-0001-…0001` | 6 (Biryani, South Indian) |
| Truffles | `a1b2c3d4-0002-…0002` | 5 (Burgers, Continental) |
| Vidyarthi Bhavan | `a1b2c3d4-0003-…0003` | 5 (Dosa, Breakfast) |

Seed customer (for `POST /api/v1/orders`): `c1b2c3d4-0001-4000-8000-000000000001` (Priya Sharma, Customer).
