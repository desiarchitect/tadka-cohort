# ADR-011: Idempotency for Unsafe Writes (`Idempotency-Key` on `POST /orders`)

**Date:** 2026-06-02
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

`POST /api/v1/orders` is not safe to repeat: each call creates a new order. But the real world *will* repeat it. A customer on a flaky Bengaluru mobile network taps "Place Order", sees a spinner, and taps again. The app retries on a dropped connection. A load balancer re-sends a request whose response was lost. The result is the **same break**: two (or three) identical orders, the customer billed twice, support tickets, refunds.

This is a property of every unsafe write over an unreliable network — "at-least-once delivery" is the default of the internet. We need the *effect* to be exactly-once even when *delivery* is at-least-once. The decision must be made now, because it shapes the request contract (a header) and the data model (a stored key), and because the same pattern returns — harder — once Payment is a separate service (Week 5) and a retried call crosses a network boundary.

## Decision

**Support a client-supplied `Idempotency-Key` request header on unsafe writes, starting with `POST /orders`.**

- The client generates a unique key (a UUID) per logical operation and **reuses the same key on retries**.
- The server stores `key → orderId` in an `ordering.idempotency_keys` table, written **in the same transaction** as the order, so the key and the order it created commit together or not at all.
- A request whose key has been seen before returns the **original order** (HTTP `200`) instead of creating a new one. A first-time key creates the order and returns `201`.
- The key column is the **primary key** (a unique constraint), so even a true concurrent double-submit cannot create two orders — the database rejects the second insert.

This mirrors Stripe's and Razorpay's public idempotency model, so it is familiar to anyone who has integrated a payment gateway.

## Consequences

### Positive
- **Exactly-once effect over at-least-once delivery.** Retries, double-taps, and LB re-sends become safe.
- **Client-controlled scope.** The client decides what "the same operation" means by choosing when to reuse a key.
- **Sets up the distributed story.** The same header + store pattern is exactly what protects a retried cross-service call after extraction — students meet it now in the easy (single-DB) setting.

### Negative
- **Clients must participate.** A client that omits the key gets the old at-least-once behaviour. We make the header optional for now (not every caller is updated on day one), which means the guarantee is opt-in.
- **Keys must be stored and eventually pruned.** The table grows; old keys need a retention/TTL policy.

### Risks
- **Same key, different body.** A client could reuse a key for a genuinely different order. **Mitigation (future):** also store a hash of the request and reject a key replay whose body differs (`422`). Deferred for Day 4 to keep the lesson focused.
- **In-flight concurrent first use.** Two requests with a brand-new key arrive simultaneously. **Mitigation:** the unique PK means one insert wins and the other fails the transaction; the loser can safely retry and will then read the winner's order.

### Cost (₹ / effort)
Near-zero infrastructure — one small table and a header read. The cost is **discipline** (clients must send and reuse keys) and a future **retention job**. Cheap insurance against duplicate-order refunds and the support load they create.

## Alternatives Considered

### Option A: Do nothing (rely on the client not to double-submit)
- Pros: zero work.
- Cons: the duplicate-order break is guaranteed at scale; you pay in refunds and trust.
- Why rejected: the failure is observed and reproducible (we demo it). Ignoring it is choosing the bug.

### Option B: Server-side dedup by "natural key" (same customer + restaurant + items within N seconds)
- Pros: no client change.
- Cons: heuristic and wrong at the edges — a customer legitimately ordering the same thing twice is blocked; the window is arbitrary.
- Why rejected: guesses intent. An explicit client key states intent exactly.

### Option C: Make the operation naturally idempotent (client generates the order id, `PUT /orders/{id}`)
- Pros: REST-pure; the id *is* the idempotency key.
- Cons: leaks id generation to clients; awkward with server-side concerns; larger contract change.
- Why rejected: the header approach is additive and matches the gateway idiom the team already knows. Revisit if we move to client-generated ids.

## References
- ADR-005: REST for Client-Facing API · ADR-009: denormalized order items
- `docs/api-design-guide.md` (Idempotency section) · `docs/api-contracts.md` (`POST /orders`)
- Stripe / Razorpay idempotency-key documentation
- Implementation: `Domain/Orders/IdempotencyKey.cs`, `Data/Repositories/IIdempotencyStore.cs`, `Controllers/OrdersController.cs`

## Revisit When
When a retried call **crosses a service boundary** (Week 5 — Order → Payment): the key must be propagated and the store may need to be shared or per-service. Also when we add **body-hash validation** (reject mismatched replays) or a **retention/TTL** policy as the table grows.
