# Day 3 changelog (since Day 2)

`git checkout day-03`. Previous branch: `day-02`.

## We learned

- REST `/api/v1`, **no PUT/DELETE** in this API (create + PATCH status).
- **Server-side pricing** — the client does not send the bill.
- Order **state machine**. RFC 7807 errors. Two-layer validation.

## Architecture

- Still one process. Full HTTP surface (~14 endpoints).

## Code vs Day 2

| Area | What changed |
|---|---|
| `Controllers/*` | `/api/v1` resources |
| Order aggregate | State transitions |
| ProblemDetails | RFC 7807 |

ADRs **005, 006, 007, 009, 010**.
