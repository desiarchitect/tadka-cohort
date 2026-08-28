# ADR-013: In-Process Domain Events

**Date:** 2026-06-02
**Status:** Accepted
**Deciders:** Tadka Engineering Team

**Topic:** After confirm, SMS (and later payment, dispatch) must run. Where, so a failed SMS cannot un-confirm the order?

**Options:**
1. Call SMS inside `Order.Confirm()` / the same transaction.
2. After `SaveChanges`, in-process handlers on a raised fact (`OrderConfirmed`).
3. Kafka today.
4. Outbox today.

**Choice:** Option 2. Aggregate records past-tense events, depends on nothing. Dispatcher runs **after** persist. Handler logs “SMS sent.” Same event shape we will put on a bus in Week 5.

**Why:** A notification failure must not roll back cooking the food. `Order` must not know SMS/payment/delivery. Kafka inside one process buys nothing (Day 1: right tool, wrong time). Outbox is the next seam when losing the event means losing money.

**Trade-off:** Dispatch is implicit (must be documented). **At-most-once:** crash between commit and handler loses the SMS. Acceptable today because the sender dies with the process.

**Failure mode:** Someone puts the HTTP call back inside the transaction “to be safe” — SMS fail un-confirms, and the lock is held for the gateway’s latency (pool). Or we pretend in-process is exactly-once.

**Revisit when:** The handler lives in another service. Then Outbox in the same WAL as the order, relay to Kafka, Inbox on the consumer. Event shape and aggregate stay.
