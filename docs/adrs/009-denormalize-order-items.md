# ADR-009: Snapshot Name and Price on Order Lines

**Date:** 2026-05-30
**Status:** Accepted
**Deciders:** Tadka Engineering Team

**Topic:** When we store an order line, do we JOIN live `restaurant.menu_items` for name and price, or copy them?

**Options:**
1. Store only `menu_item_id`; JOIN at read time (normalized).
2. Copy `name`, `unit_price`, `currency` onto `ordering.order_items` at place-order. Keep `menu_item_id` for analytics, not for the receipt.

**Choice:** Option 2. Snapshot. Same idea as delivery address on the order.

**Why:** The receipt is history. A rename or a ₹299 → ₹349 must not rewrite last Tuesday's bill. JOIN would be a cross-schema read (banned as a *live* dependency) and would break the day Ordering is its own database.

**Trade-off:** Duplicated strings. At 1 lakh orders × 3 lines this is megabytes, not a warehouse problem. Typos in the menu stay on old receipts — correct for orders, noisy for a partner scrolling history.

**Failure mode:** Someone "normalizes it back" to stop duplication and yesterday's orders start showing today's price. Silent money bug.

**Revisit when:** Never for unit price and the name as charged. Catalog analytics can still use `menu_item_id`.
