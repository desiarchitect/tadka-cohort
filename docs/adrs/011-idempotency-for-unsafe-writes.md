# ADR-011: Idempotency for Unsafe Writes

**Date:** 2026-06-02
**Status:** Accepted
**Deciders:** Tadka Engineering Team

**Topic:** Double-tap / retry on `POST /orders` creates two orders. How do we make the *effect* once when the network delivers at-least-once?

**Options:**
1. Disable the button (client only).
2. Dedup “same user, same restaurant, last N seconds.”
3. Client `Idempotency-Key` header + unique constraint, same transaction as the order.
4. Store the key in Redis, order in Postgres.

**Choice:** Option 3. Client generates a UUID per logical place-order and reuses it on retry. Server stores `key → orderId` in `ordering.idempotency_keys`. First request `201`, replay `200` same body. Unique constraint is the race fix.

**Why:** A button cannot stop a request already on the wire. Time-window dedup kills two coworkers ordering the same biryani. Check-then-insert is the race. Redis + Postgres are two commits (two WALs) that can disagree. One fsync, key and order together.

**Trade-off:** Keys table needs TTL/cleanup. Header is opt-in; omit it and you are back to two orders. Client SDK must reuse the key.

**Failure mode:** Client mints a new key on every retry (header is decoration). Same key, different body: must `422`/`409`, not silently return the old order (Stripe). Process dies *before* commit: retry is correctly the first attempt — window narrowed, not closed.

**Revisit when:** The handler is another service (Payment extract). Same header; store still cannot be a different database than the write it protects without an Outbox.
