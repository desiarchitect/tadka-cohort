# ADR-045: Pessimistic Locking for the Hot-Row Coupon Redemption Path

**Date:** 2026-07-12
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

ADR-012 chose optimistic concurrency (`xmin`) for `Order` because two writers hitting the
*same order* at the *same instant* is rare at Tadka's scale — optimise for the common
no-conflict path. ADR-012 also names its own exception, in "Revisit When": *"when an order row
becomes genuinely hot... switch that path to pessimistic locking."*

A limited-use coupon (`TADKA50`, 100 redemptions) is exactly that hot row. Unlike an order,
every customer trying to redeem the coupon writes the *same* row — this is the flash-sale /
Tatkal-ticket access pattern, not the "two tabs on my own order" pattern ADR-012 was written for.

## Decision

**Use `SELECT ... FOR UPDATE` (pessimistic locking) for coupon redemption, not `xmin`
optimistic concurrency** — and prove the choice with a runnable, captured comparison rather
than asserting it.

`CouponsController` exposes three redemption strategies against the identical seeded coupon so
the break kit can run the same concurrent-load script against each and compare:
`POST /api/v1/coupons/{code}/redeem/{none|optimistic|pessimistic}`.

## Captured Evidence (50 concurrent redeems, laptop, `scripts/coupon-concurrency-demo.js`)

| Strategy | HTTP results | DB ground truth | Verdict |
|---|---|---|---|
| **None** (no guard) | 50× 200 | `Redeemed` counter = **9**, `coupon_redemptions` rows = **50** | **BROKEN** — 41 lost updates; oversold 50 vs a counter of 9 |
| **Optimistic** (`xmin`, ADR-012's default) | 2× 200, **48× 409** | counter = 2, rows = 2 (consistent) | Correct, but a **409 retry storm**: 96% of legitimate concurrent requests fail and must retry client-side |
| **Pessimistic** (`FOR UPDATE`, this ADR) | **50× 200** | counter = 50, rows = 50 (consistent) | Correct, **zero retries needed** — requests serialize and queue instead of racing |

At 150 concurrent requests (over the 100-redemption cap), pessimistic locking returned exactly
**100× 200 and 50× 422** — the scarcity limit is honored precisely even under 1.5x overload,
because the row lock serializes access to the counter itself.

## Consequences

### Positive
- **Correct under contention, with no client-side retry burden.** Every request either succeeds
  or gets a definitive `422 Exhausted` — never a `409` requiring a retry loop the frontend has to
  implement. This matters specifically because ALL requests target one row (unlike Orders).
- **Confirms, not contradicts, ADR-012.** Optimistic concurrency stays correct for Orders; this
  ADR draws the line the earlier one predicted, backed by numbers instead of guesswork.

### Negative
- **Row lock held for the transaction's duration.** A slow operation inside the same transaction
  (see ADR-013 / the Day 4 lock-hold beat) would now stall every other redeemer, not just corrupt
  data. Keep the locked transaction short — no external calls inside it.
- **Throughput ceiling.** Serialized writes cap redemption throughput to one at a time on this
  row. Acceptable for a scarce 100-unit coupon; would need a different design (e.g. a
  pre-partitioned counter, or Redis `DECR`) at flash-sale scale (lakhs of concurrent redeemers).

### Risks
- **Deadlocks** if a transaction ever locks the coupon row and *then* tries to lock another
  contended row in a different order elsewhere in the code. **Mitigation:** the coupon
  transaction does nothing else — lock, check, increment, insert, commit.

## Alternatives Considered

### Option A: No locking (status quo before this ADR)
- Rejected on evidence: 41/50 redemptions silently lost (counter under-counts by 82%), and the
  redemption row count *exceeds* the coupon's stated limit — an oversell a finance team would
  actually notice.

### Option B: Optimistic concurrency (ADR-012's pattern, reused as-is)
- Correct data, but 96% of concurrent requests fail with 409 at just 50 concurrent redeemers.
  Every 409 needs a client retry loop, and retries under contention just re-race the same hot
  row — the retry storm ADR-012 itself flagged as the risk to watch for.
- Why rejected here specifically (kept elsewhere): the access pattern is the one ADR-012 named
  as its own exception, not a general reversal of that decision.

### Option C: Redis `DECR` as an atomic counter, Postgres for the row of record
- Pros: sub-millisecond, no DB row lock at all; the real answer at genuine flash-sale scale.
- Cons: two systems of record (Redis counter vs Postgres redemption rows) that can drift on a
  crash between the two writes; needs the transactional-outbox-grade discipline Week 5 introduces.
- Why rejected for now: premature at 100 coupons / class-demo scale; revisit if a real flash sale
  needs lakhs of concurrent redeemers (see Revisit When).

## References
- ADR-012: Optimistic Concurrency for Order State Transitions (the pattern this ADR deliberately
  does NOT reuse, and why)
- `Controllers/CouponsController.cs`, `Data/Configurations/CouponConfiguration.cs`
- `scripts/coupon-concurrency-demo.js` — reproduces the captured numbers above
- Break kit: `cohort-prep/day-04/break-kit-day-04.md`, Demo 5

## Revisit When
If a coupon needs to serve **lakhs** of concurrent redeemers (a real flash sale, not a 100-unit
class demo), a single Postgres row lock becomes the bottleneck itself — move to Option C
(Redis atomic counter + async reconciliation) or a sharded-counter pattern.
