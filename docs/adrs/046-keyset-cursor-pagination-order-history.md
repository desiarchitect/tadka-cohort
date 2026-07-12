# ADR-046: Keyset (Cursor) Pagination for Order History

**Date:** 2026-07-12
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

`GET /api/v1/orders` (offset pagination, Day 3) works fine on a fresh account with a handful of
orders. A loyal customer with thousands of orders scrolling to the bottom of their history is a
different query: `OFFSET 3990 LIMIT 10` still has to fetch and sort every one of the first 4,000
matching rows before it can discard 3,990 of them and return the last 10. The cost is **O(offset)**,
not O(page size) — page 2 is nearly free, page 800 is not, and it gets worse every day the table
grows. This is a correctness-adjacent scaling issue (ADR-014's index doesn't fix it — the index
still has to be walked and sorted N times), distinct from and cheaper to fix than reaching for
partitioning (ADR-017).

## Decision

**Add keyset ("cursor") pagination as a second, purpose-built endpoint —
`GET /api/v1/orders/history` — for customer-facing order history, and leave the existing offset
`GET /api/v1/orders` unchanged for small, page-numbered views.**

- The cursor is an opaque, base64-encoded `(CreatedAt, Id)` pair — the exact composite the
  `(customer_id, created_at DESC)` index (ADR-014) is built on, plus `Id` as a tie-breaker for
  rows sharing a timestamp (real seeded data has exact-timestamp collisions; without a
  tie-breaker, ties can be skipped or repeated across pages).
- The query becomes an inequality (`WHERE (created_at, id) < (cursor_created_at, cursor_id)`)
  instead of `Skip(N)` — an index range scan, not "walk N rows and throw them away."
- A page fetches `pageSize + 1` rows to determine `NextCursor` without a separate `COUNT(*)` or
  lookahead query.

## Captured Evidence (200k-row seed, one customer's ~4,000 orders, depth = row 3,990)

| | Offset (`OFFSET 3990 LIMIT 10`) | Keyset (same depth) |
|---|---|---|
| Plan shape | `Sort` over **4,000** fetched rows, discard 3,990 | `Sort` over **10** rows after the cursor (`Rows Removed by Filter: 212`) |
| Execution time | **150.5 ms** | **2.0 ms** |

**~74x faster at the identical depth**, using the same index either way — the difference is
entirely in how many rows the query has to touch before it can answer.

## Consequences

### Positive
- No cost cliff at any scroll depth — the query does the same small amount of work on page 2 and
  page 8,000.
- Reuses the existing ADR-014 index; no new index, no new column, no migration.

### Negative
- **No jump-to-page-N.** Only next/previous. Wrong fit for a page-numbered admin table; right fit
  for a scrolling feed (which is what order history actually is).
- **No free `TotalCount`/`TotalPages`.** Computing a total means counting the whole matching set —
  the exact cost this ADR exists to avoid. `CursorPageResponse<T>` deliberately omits it; a caller
  that truly needs a total pays for that query explicitly and separately.
- **Two endpoints, two response shapes** (`PagedResponse<T>` vs `CursorPageResponse<T>`) to
  maintain instead of one. Accepted: they answer genuinely different questions ("what's on page
  N" vs "what's next"), and conflating them into one polymorphic response is worse than two clear ones.
- The cursor is opaque but not encrypted/signed — a client could hand-construct one. Low risk: it
  only ever narrows results to rows the caller is already authorized to see (scoped by
  `customerId`), same trust boundary as any other query parameter.

### Risks
- A caller could persist a cursor and hand it back after (customer_id, created_at, id) semantics
  changed. **Mitigation:** none needed today (these columns are immutable post-insert); revisit if
  that ever changes.

## Alternatives Considered

### Option A: Keep offset pagination everywhere, tell customers to filter by date instead
- Doesn't fix the underlying cost, just hides it behind a UX workaround the product didn't ask for.
- Rejected: the real fix is one query shape, not a policy asking users to route around a bug.

### Option B: Partition `orders` by `created_at` (ADR-017) to speed up deep pages
- Partitioning helps *unfiltered/wide* scans (Day 5 Beat 4), but does nothing for the specific
  "sort N rows to throw away N-10 of them" cost of `OFFSET` — a keyset query on a partitioned OR
  unpartitioned table has the same OFFSET problem if it still uses `Skip()`.
- Rejected as the fix *for this symptom* specifically: it's the wrong tool, and it's a schema
  migration for a problem a query-shape change already solves for free.

### Option C: Replace `GET /orders` entirely with cursor-only pagination
- Rejected: small admin/back-office views genuinely benefit from jump-to-page-N and a visible
  total, and their result sets are small enough that OFFSET's cost never bites. Forcing every
  caller onto cursors removes a feature (page jump) some callers actually need.

## References
- ADR-014: indexing strategy (the index this ADR's query plan reuses)
- ADR-017: partitioning deferred (a different fix, for a different symptom — see Alternatives)
- `Controllers/OrdersController.cs` (`GetByCustomerCursor`), `Contracts/CursorPageResponse.cs`,
  `Contracts/Orders/OrderCursor.cs`
- Break kit: `cohort-prep/day-05/break-kit-day-05.md`, Beat 5

## Revisit When
If a caller genuinely needs jump-to-page-N on a large, customer-facing result set (not observed
today), evaluate a hybrid (approximate total via `EXPLAIN`'s row estimate, or a periodically
refreshed count) rather than reverting to `OFFSET` at scale.
