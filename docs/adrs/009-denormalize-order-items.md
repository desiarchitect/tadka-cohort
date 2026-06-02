# ADR-009: Denormalize Name and Price in Order Items

**Date:** 2026-05-30
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

When a customer places an order, the order items reference menu items from the restaurant's current menu. The question is: should `ordering.order_items` store the menu item's name and price at the time of ordering, or should it store only the `menu_item_id` and JOIN to `restaurant.menu_items` at read time?

This is a classic tension between normalization (single source of truth, no duplication) and historical accuracy (what was true at the time of the event).

## Decision

Copy `name` and `unit_price` from the menu item into `ordering.order_items` at order creation time. The order item stores a snapshot of the relevant menu item data as it was when the customer placed the order.

**Columns on `ordering.order_items`:**
- `menu_item_id` — reference to the original menu item (for analytics, not for display)
- `name` — copied from `menu_items.name` at order time
- `unit_price` — copied from `menu_items.price` at order time
- `currency` — copied from `menu_items.currency` at order time

## Consequences

### Positive

- **Historical accuracy.** A restaurant renames "Chicken Biryani" to "Hyderabadi Chicken Dum Biryani" next week. Orders from last week still show "Chicken Biryani" at the price the customer paid. The customer's receipt is a snapshot, not a live view.
- **No cross-schema JOIN for order display.** Displaying an order doesn't require joining to `restaurant.menu_items`. The order is self-contained. When we extract the ordering service to its own database, this query still works.
- **Price integrity.** Restaurant increases biryani price from ₹299 to ₹349. Existing orders still show ₹299. The customer was charged ₹299, and the order reflects that. No silent price changes on historical orders.
- **Service extraction ready.** The ordering service doesn't need to call the restaurant service to display an order. All data needed for the order receipt is in the ordering schema.

### Negative

- **Data duplication.** The item name is stored in both `restaurant.menu_items` and `ordering.order_items`. For 1 lakh orders with 3 items each, that's 3 lakh name strings. At ~50 bytes per name, that's ~15 MB. Negligible.
- **Stale data by design.** If a restaurant fixes a typo in the item name, past orders still show the typo. This is correct behavior for orders but might confuse restaurant owners reviewing their order history.

### Risks

- **Risk:** Developers JOIN to `menu_items` for order display out of normalization habit, bypassing the denormalized data. **Mitigation:** Code reviews. The order response DTO maps from `OrderItem.Name`, not from a JOIN. The API never exposes the current menu item data on order responses.
- **Risk:** The denormalized fields grow as we need more menu item details on orders (description, category, dietary tags). **Mitigation:** Only copy fields that appear on the customer's receipt. Name and price are the minimum. Description and category can be fetched from the restaurant service if needed for analytics.

## Alternatives Considered

### Option A: Store Only menu_item_id, JOIN at Read Time
- Pros: Fully normalized. Single source of truth for item names and prices. No duplication.
- Cons: Menu item changes retroactively alter order history. Customer ordered "Chicken Biryani" at ₹299, restaurant renames it and changes price, customer's order history now shows "Hyderabadi Chicken Dum Biryani" at ₹349. This is a bug, not a feature. Also requires a cross-schema JOIN from `ordering` to `restaurant` for every order display.
- Why rejected: Historical accuracy matters for receipts, invoices, and customer trust. Normalization is wrong here.

### Option B: Store Full Menu Item Snapshot (All Fields)
- Pros: Complete historical record. Every field from the menu item is preserved.
- Cons: Overkill. The order receipt doesn't need the item's description, category, or availability flag. Storing 10 fields when 2 are needed wastes space and creates confusion about which fields are "official" on the order.
- Why rejected: Copy what appears on the receipt, nothing more. Name and price. If we need more later, we add columns, but YAGNI.

## References

- ADR-003: Schema-Per-Domain (order_items is in the ordering schema, menu_items is in the restaurant schema)
- ADR-008: No Cross-Schema Foreign Keys (menu_item_id is a soft reference, not a FK)
- Event sourcing principle: capture facts at the time they happen
- Standard practice at Amazon, Flipkart, Swiggy for order/invoice systems

## Revisit When

When load testing or business requirements prove this decision is a bottleneck, or when specific pain points mentioned in 'Risks' are realized.
