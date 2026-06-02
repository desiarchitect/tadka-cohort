# Database Indexing Strategy

> **Architect's Rule:** Never index blindly. Every index slows down writes and consumes memory. Index only when you have a proven read bottleneck (identified via `EXPLAIN ANALYZE`) where the read:write ratio justifies the cost.

## Tadka's Indexing Map (Phase A1.2)

### The `ordering.orders` Table
This is our most heavily queried table. A single `Seq Scan` here can take down the database during peak hours.

1. **`IX_orders_customer_id` (B-Tree)**
   - **Why:** Supports the "Order History" page. When a customer logs in, we immediately fetch their recent orders. Without this, Postgres must scan every order in history.
   - **Type:** Single-column B-Tree.

2. **`IX_orders_restaurant_id` (B-Tree)**
   - **Why:** Supports the Restaurant Admin Dashboard. Restaurants need to see all active orders assigned to them instantly.
   - **Type:** Single-column B-Tree.

3. **`IX_orders_customer_id_created_at` (Composite B-Tree, Descending)**
   - **Why:** Our API uses pagination for order history (`ORDER BY CreatedAt DESC`). By baking the sort order directly into a composite index, Postgres can skip the `Sort` node entirely and stream results directly from the index.
   - **Performance Gain:** Eliminates in-memory sorting, saving CPU and `work_mem`.

### The `ordering.restaurants` Table
Restaurants are read constantly (customer apps) but written to rarely (onboarding).

1. **`IX_restaurants_is_active` (Partial B-Tree)**
   - **Why:** Customers only care about *active* restaurants. By using a partial index (`WHERE is_active = true`), we keep the index incredibly small and fast, excluding closed or banned restaurants entirely.
   - **Performance Gain:** Reduces index size and memory footprint.

2. **`IX_restaurants_address_city` (B-Tree)**
   - **Why:** The primary entry point for a customer is searching for restaurants in their city.

## When NOT to Index
- **`Status` on Orders:** We do not index `Status` because its cardinality is extremely low (only ~5 possible values), and the values are heavily skewed (95% of orders are `Delivered`). Postgres would likely ignore an index on this column anyway in favor of a Seq Scan.
