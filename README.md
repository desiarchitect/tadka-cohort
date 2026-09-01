# Tadka 🍛

A food delivery platform built as a teaching project for the [Desi Architect](https://desiarchitect.com) cohort.

This branch is **Day 5**: Saturday's API plus **performance indexes**, a **tuned connection pool**, a **streaming read replica** (EF read/write split), a **partition SQL experiment**, and **keyset order history**. Redis is **not** here yet (Day 6). The client still does not send prices.

## Where this is going

Over 8 weeks this monolith evolves into **4 services plus a gateway**. Destination: [`docs/diagrams/day-01-final-architecture.md`](docs/diagrams/day-01-final-architecture.md).

Right now:

```
Client  →  Tadka.Api (/api/v1)  →  PostgreSQL 16 primary  :5432  (writes + read-your-writes)
                                 →  PostgreSQL 16 replica  :5433  (stale-tolerant GETs)
```

No Redis, Kafka, gateway, or Payment HTTP API yet.

## Tech stack (today)

- **.NET 10** — Web API with Controllers
- **PostgreSQL 16** — primary + streaming replica
- **EF Core** — `TadkaDbContext` (writes) + `TadkaReadDbContext` (replica, NoTracking)
- **FluentValidation** — request validation (400)
- **xUnit + Testcontainers** — state machine + order-flow + cursor tests
- **Docker Compose** — two Postgres containers

## Getting started

Prerequisites: [`SETUP.md`](SETUP.md).

```powershell
git checkout day-05

# Switching from Day 4: wipe so the replica clones a fresh primary.
docker compose down -v
docker rm -f tadka-postgres tadka-postgres-replica
docker volume rm tadka_pgdata tadka_pgdata_replica tadka-cohort_pgdata
docker compose up -d
docker compose ps              # tadka-postgres AND tadka-postgres-replica (healthy)

dotnet test Tadka.slnx         # 23 cases; needs Docker Desktop
dotnet run --project src/Tadka.Api

curl.exe http://localhost:5224/health
curl.exe http://localhost:5224/api/v1/restaurants
```

HTTP **5224**. Compose service **`postgres`** (primary) and **`postgres-replica`**. Dev also serves Scalar at `http://localhost:5224/scalar`.

Full curls, 200k seed, EXPLAIN, replica lag, partition demo, history cursor: [`docs/runbooks/day-05.md`](docs/runbooks/day-05.md). Use `curl.exe` and `--data-binary "@docs/runbooks/place-order.json"`.

`Password=tadka_local` in `appsettings.Development.json` is a **local dummy**.

## What this branch added

| ADR | What |
|---|---|
| 014 | Composite index `(CustomerId, CreatedAt DESC)` + `CreatedAt DESC`. Not `status` / `restaurant_id`. |
| 015 | Npgsql `Min 5 / Max 50`. PgBouncer is Day 11. |
| 016 | Replica `:5433` + `TadkaReadDbContext`. `GET /orders/{id}` stays on primary. |
| 017 | Do **not** partition `ordering.orders`. Demo is throwaway SQL. |
| 046 | `GET /api/v1/orders/history` keyset cursor. Offset `GET /orders` stays for small admin pages. |
| 013 | Still here from Day 4: `OrderConfirmed` after `SaveChanges`. |

## Project structure

```
tadka/
├── src/Tadka.Api/
│   ├── Controllers/
│   ├── Data/                   # TadkaDbContext + TadkaReadDbContext
│   └── Domain/
├── tests/Tadka.Api.Tests/
├── scripts/                    # 200k seed, induce-break, apply-fix, partition demo, measure-load.ps1
├── toydemo/day-03-api-primitives/cursor-pagination-toy/   # OFFSET vs keyset (taught today)
└── docs/runbooks/day-05.md
```
