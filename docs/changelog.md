# Day 4 changelog (since Day 3)

`git checkout day-04`. Previous branch: `day-03`.

## We learned

- **Idempotency** for unsafe writes (`Idempotency-Key`).
- **Optimistic concurrency** (`xmin` → 409).
- **In-process domain events** after commit (hand-rolled dispatcher — Day 7 replaces with MediatR).
- Integration tests with Testcontainers.

## Architecture

- Still one process. Events do not leave the process.

## Code vs Day 3

| Area | What changed |
|---|---|
| Idempotency store | Unsafe POST/PATCH |
| Order `xmin` | 409 on stale write |
| Domain events | After `SaveChanges` |
| `tests/` | Testcontainers |

ADRs **011, 012, 013**.
