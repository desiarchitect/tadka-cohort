# Tadka — Student Runbooks (Day 1 → Day 6)

Hands-on, copy-paste guides to **run, demo, and verify each day's code yourself**. One file per day; each reflects that day's branch state (the system grows as you go — Day 3 has no cache/replica yet, Day 5 adds the replica, Day 6 adds Redis). Every architectural move is *earned by a failure you can reproduce* — these runbooks show you how.

| Day | What you build & verify | Runbook |
|----|----|----|
| 1 | Scaffold + `/health`; Copilot setup | [day-01.md](day-01.md) |
| 2 | Domain model + schema-per-domain (5 schemas, migration) | [day-02.md](day-02.md) |
| 3 | Full REST API `/api/v1` (14 endpoints), server-side pricing, state machine, RFC 7807 errors | [day-03.md](day-03.md) |
| 4 | Hardening: idempotency, optimistic concurrency (409), domain events; integration tests | [day-04.md](day-04.md) |
| 5 | Scaling: indexes (EXPLAIN), connection pool, streaming **read replica** + load test | [day-05.md](day-05.md) |
| 6 | **Redis** cache-aside + stampede lock + invalidation, and **SSE live tracking** over a Redis backplane | [day-06.md](day-06.md) · CLI walkthrough [redis-cli.md](../database/redis-cli.md) |

> Consolidated demo index (issue → fix → trade-off → captured numbers): the instructor pack's `cohort-prep/DEMOS.md`.

---

## Prerequisites (install once)

- **Git**, **.NET 10 SDK** (`dotnet --version` → 10.x), **Docker Desktop** (running).
- **Optional:** an HTTP GUI (the app ships **Scalar** at `https://localhost:7036/scalar/v2`), `k6` (Day 5 load test — `winget install GrafanaLabs.k6`), `psql`/`redis-cli` (or just use `docker exec`).

## One-time setup

```bash
git clone <tadka-repo-url> tadka
cd tadka
```

Each day is a **branch**. Switch to the day you're working on:

```bash
git checkout day-03      # day-01 … day-06
```

## The shape of every day

```bash
docker compose up -d                      # start infra (Postgres; +replica Day 5; +Redis Day 6)
dotnet run --project src/Tadka.Api        # builds, applies migrations, seeds, starts the API
# → app on http://localhost:5224 (https 7036). Browse/try APIs at https://localhost:7036/scalar/v2
dotnet test                               # from Day 4 on, runs the test suite (needs Docker for Testcontainers)
```

Stop the app with `Ctrl+C`. Reset the database completely with `docker compose down -v` (wipes volumes) then `up -d`.

## Connection facts (same every day)

| Thing | Value |
|---|---|
| API (http / https) | `http://localhost:5224` / `https://localhost:7036` |
| API docs (Scalar UI) | `https://localhost:7036/scalar/v2` |
| Postgres (primary) | `localhost:5432`, db `tadka`, user `tadka`, pass `tadka_local` |
| Postgres replica (Day 5+) | `localhost:5433` |
| Redis (Day 6+) | `localhost:6379` |

## Seed data (created automatically on first run)

| Entity | Id | Note |
|---|---|---|
| Restaurant — Meghana Foods | `a1b2c3d4-0001-4000-8000-000000000001` | 6 menu items |
| Restaurant — Truffles | `a1b2c3d4-0002-4000-8000-000000000002` | 5 items |
| Restaurant — Vidyarthi Bhavan | `a1b2c3d4-0003-4000-8000-000000000003` | 5 items |
| Menu item — Chicken Biryani (Meghana) | `b1b2c3d4-0001-4000-8000-000000000001` | ₹299 |
| Customer — Priya Sharma | `c1b2c3d4-0001-4000-8000-000000000001` | use as `customerId` |

## ⚠️ Windows PowerShell + curl

In **Windows PowerShell**, `curl` is an alias for `Invoke-WebRequest`. Use **`curl.exe`**. Quote `"@docs/runbooks/place-order.json"`. Do not paste `-d "{...}"`.

## Common gotchas

- **First `/health` is slow** (~500–800 ms): EF model build + connection-pool warm-up + JIT. Hit it again for the real (single-digit ms) number.
- **`docker compose up` says a container name is in use:** `docker rm -f <name>` then `up -d`, or `docker compose down` first.
- **Port already in use (5224/5432):** stop the previous `dotnet run` / container.
- **Migrations** run automatically at startup against the **primary**; you don't run `dotnet ef` yourself.
