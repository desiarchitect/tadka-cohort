# Tadka — Student runbooks

Copy-paste guides to run and verify **this branch's** code. Each day ships the runbook for that day (and earlier days as history).

| Day | What you run | Runbook |
|-----|----------------|---------|
| 1 | Scaffold + liveness `/health`; Copilot adds `/health/ready` | [day-01.md](day-01.md) |
| 2 | Domain model + 5 Postgres schemas; `/health/ready` already in the controller | [day-02.md](day-02.md) |
| 3 | REST `/api/v1`, server-side pricing, RFC 7807, seed | [day-03.md](day-03.md) |
| 4 | `Idempotency-Key`, `xmin` → 409, in-process domain events | [day-04.md](day-04.md) |
| 5 | Indexes, connection pool, streaming read replica, partitioning | [day-05.md](day-05.md) |

Later days add their own file here when that day's code exists.

**Windows:** use `curl.exe`, not `curl`. New to Compose or Copilot context files? [docs/learn/](../learn/README.md).
