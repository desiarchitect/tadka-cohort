# ADR-012: Optimistic Concurrency for Order State Transitions (PostgreSQL `xmin`)

**Date:** 2026-06-02
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

An order's status moves through a state machine (`Created → Confirmed → Preparing → …`). Two actors can act on the **same order at the same instant**: a customer taps "Cancel" while the restaurant taps "Confirm"; the customer's app double-fires a status update; two browser tabs are open. The classic read-modify-write race follows — both requests read the order at status N, both compute a new status, both write. With no protection it is **last-write-wins**: one transition silently overwrites the other (a *lost update*).

We need to guarantee that concurrent writers cannot clobber each other unknowingly. Two failures must be distinguished cleanly, because they mean different things to the client:
- An **illegal transition** (e.g. `Created → Delivered`) is a *domain-rule* violation — already a **422** (ADR-006). The request was wrong.
- A **concurrency conflict** is *not* a wrong request — the request was legal, you simply lost a race. The correct, standard answer is **409 Conflict**: reload and retry.

This is the monolith-phase warm-up for the distributed version of the same problem (Week 5+), where the racers are different services and the answer becomes distributed locking or saga compensation.

## Decision

**Use optimistic concurrency on `Order`, backed by PostgreSQL's `xmin` system column, and map a conflict to HTTP 409.**

- Postgres already stamps every row with `xmin` — the id of the transaction that last wrote it. We map it as a **shadow concurrency token** in `OrderConfiguration` (no new column, no migration of data). EF includes `WHERE xmin = @original` in every `UPDATE`.
- If another transaction wrote the row first, our `UPDATE` matches **zero rows**; EF raises `DbUpdateConcurrencyException`.
- `ExceptionHandlingMiddleware` maps that exception to **`409 Conflict`** as RFC 7807 Problem Details, telling the client to reload and retry.

**Optimistic, not pessimistic**, because at Tadka's scale (≈1.2 orders/s, ~25/s peak) two writers hitting the *same* order in the same millisecond is **rare** — so we optimise for the common no-conflict path (no locks held) and pay a cost only when a conflict actually happens.

## Consequences

### Positive
- **No lost updates.** A stale write is rejected, not silently applied.
- **No row locks on the happy path.** Readers and writers don't block each other; throughput stays high. Ideal for low-contention data like a single order.
- **Free token.** `xmin` costs no extra column, no app-managed version field, no write amplification.
- **Honest status codes.** `409` (raced) is clearly separated from `422` (illegal) and `404` (gone) — the client knows whether to retry, fix the request, or give up.

### Negative
- **Clients must handle 409.** A well-behaved client reloads and retries; a naive one surfaces an error. The contract must document the retry expectation.
- **`xmin` is Postgres-specific.** The token wouldn't port to another database as-is. Acceptable: Postgres is a locked decision (ADR-004) and the *concept* (a rowversion token) ports even if the column doesn't.

### Risks
- **Retry storms.** Many writers on one hot order could thrash (conflict → retry → conflict). **Mitigation:** at our scale this is not a concern; if a row ever becomes hot, switch *that* path to a queue or pessimistic lock. (Revisit-when.)
- **Forgotten on new aggregates.** A future aggregate that needs concurrency safety might omit the token. **Mitigation:** it's part of the entity-config review checklist.

### Cost (₹ / effort)
Effectively zero infrastructure and zero schema cost — `xmin` is already there. The cost is a few lines of mapping config + middleware, and the **client-side discipline** to retry on 409. Far cheaper than the alternative: debugging silent lost-update corruption in production.

## Alternatives Considered

### Option A: No concurrency control (last-write-wins)
- Pros: nothing to build.
- Cons: silent lost updates — the most expensive kind of bug because nothing errors.
- Why rejected: the break is real and demoable; "silent" makes it worse, not acceptable.

### Option B: Pessimistic locking (`SELECT … FOR UPDATE`)
- Pros: conflicts impossible; the second writer waits.
- Cons: holds a lock for the duration of the transaction; under load, writers queue and latency spikes; risk of deadlocks.
- Why rejected: optimises for the *rare* case at the cost of the *common* case. Wrong trade at low contention. Reach for it only on a proven hot row.

### Option C: App-managed version column (`int Version`, bumped on write)
- Pros: database-agnostic; explicit.
- Cons: an extra column to add, migrate, and remember to increment; duplicates what `xmin` gives free.
- Why rejected: `xmin` is the zero-cost equivalent on our chosen database. Use Option C only if we ever leave Postgres.

## References
- ADR-004: EF Core code-first · ADR-006: RFC 7807 errors · ADR-002: monolith-first
- `Data/Configurations/OrderConfiguration.cs` (xmin token) · `Middleware/ExceptionHandlingMiddleware.cs` (409 mapping)
- Tests: `OrderFlowIntegrationTests.xmin_concurrency_token_rejects_a_stale_write`
- Npgsql concurrency-token documentation

## Revisit When
When an order row becomes **genuinely hot** (many concurrent writers — unlikely for a single order, possible for a shared aggregate), switch that path to **pessimistic locking or a serialized queue**. And at **service extraction (Week 5+)**, where the racers are separate services: single-row optimistic concurrency no longer spans the boundary, and the problem becomes **distributed locking / saga compensation**.
