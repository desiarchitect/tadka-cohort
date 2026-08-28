# ADR-012: Optimistic Concurrency on Orders (`xmin`)

**Date:** 2026-06-02
**Status:** Accepted
**Deciders:** Tadka Engineering Team

**Topic:** Restaurant confirms while the customer cancels, same instant. How do we stop a silent lost update, and which status code does the loser get?

**Options:**
1. Last-write-wins (do nothing).
2. SERIALIZABLE on the whole transaction.
3. `SELECT … FOR UPDATE` (pessimistic) on every transition.
4. Optimistic token: Postgres `xmin` on `UPDATE … WHERE xmin = @original`; conflict → **409**.
5. Application lock / Redis lock.

**Choice:** Option 4. No extra column. EF concurrency token on `xmin`. Illegal transition stays **422**; lost race is **409** (reload, then retry).

**Why:** At Tadka contention on *one order* is rare (customer, restaurant, rider). Optimistic is free on the no-conflict path. MVCC already versions the row; MySQL would need a `version` column. 422 vs 409 is whether the *same* request can succeed after a reload.

**Trade-off:** Clients must handle 409. SERIALIZABLE would stop the anomaly and tax every transaction plus force retries anyway. Pessimistic works on a hot row (Tatkal) and **kills the pool** if the lock spans a network call (Day 7).

**Failure mode:** Client retries 409 without reload → loop. `FOR UPDATE` held across a payment HTTP call → connections stuck, whole app sits, no bug in your code.

**Revisit when:** One row, thousands of writers (BookMyShow seat, IRCTC berth) — then a queue or pessimistic **on that row**, not “always optimistic.”
