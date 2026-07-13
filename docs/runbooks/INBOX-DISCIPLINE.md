# Inbox discipline (Day 9 invariant)

From the day Kafka consumers ship:

1. If `messageId` is already in `inbox_messages` -> skip (redelivery).
2. Do the side effect (charge, confirm, refund request) — **idempotent**.
3. Insert inbox row (+ outbox if publishing).
4. Commit the Kafka offset.

Never: stamp inbox, then do work. A crash between those steps loses the effect forever.

This rule holds on day-09, day-10, day-11, and all later days. See `DAY-EVOLUTION.md` on main/day-12+.
