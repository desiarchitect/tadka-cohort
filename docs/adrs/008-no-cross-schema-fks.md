# ADR-008: No Cross-Schema Foreign Keys

**Date:** 2026-05-30
**Status:** Accepted
**Deciders:** Tadka Engineering Team

**Topic:** `ordering.orders.restaurant_id` points at another schema. Does Postgres enforce that with a foreign key?

**Options:**
1. Cross-schema FKs everywhere "because that's what databases are for."
2. No FKs at all, even inside a schema.
3. FKs only **inside** a schema. Across schemas: IDs + application checks. Snapshot what you must remember (price, address).

**Choice:** Option 3. In-schema FKs stay (`order_items.order_id` → `orders.id`). Cross-schema: no FK. Validate in code. Copy name/price onto order lines (ADR-009).

**Why:** A FK across `ordering` and `restaurant` is a lie about the future boundary. Extraction would have to drop it under load. Cascade-delete of a user must not wipe order history. Microservices cannot have cross-DB FKs; we practise that while it is cheap.

**Trade-off:** The database will not stop an order with a garbage `restaurant_id` if the app is buggy. DBAs see "orphan" IDs. That is the cost of a seam.

**Failure mode:** A bug inserts a bad ID and we only notice at read time. Or someone "fixes" it by adding the cross-schema FK in a hurry, and Week 6 extraction inherits a lock across two future databases.

**Revisit when:** Never for money/history rows. If a context is extracted, the ID remains an ID; the FK still must not exist.
