# Tadka 🍛

A food delivery platform built as a teaching project for the [Desi Architect](https://desiarchitect.com) cohort.

This branch is **Day 4**: Saturday's `/api/v1` plus **idempotency**, **optimistic concurrency (`xmin` → 409)**, and **in-process domain events**. The client still does not send prices. Illegal transitions are **422**. A lost race is **409**.

## Where this is going

Over 8 weeks this monolith evolves into **4 services plus a gateway**. Destination: [`docs/diagrams/day-01-final-architecture.md`](docs/diagrams/day-01-final-architecture.md). There is no separate User service.

Right now:

```
Client  →  Tadka.Api (/api/v1)  →  PostgreSQL 16
                                   ├── ordering
                                   ├── restaurant
                                   ├── delivery
                                   ├── identity
                                   └── payment
```

No Redis, Kafka, gateway, or Payment HTTP API yet.

## Tech stack (today)

- **.NET 10** — Web API with Controllers
- **PostgreSQL 16** — one database, five schemas
- **EF Core** — `InitialDomainModel` + `OrderLifecycleAndDemoSeed` + `Day04Hardening` on startup
- **FluentValidation** — request validation (400)
- **xUnit + Testcontainers** — state machine + order-flow tests (**24/24**)
- **Docker Compose** — Postgres only

## Getting started

Prerequisites: [`SETUP.md`](SETUP.md). Compose: [`docs/learn/docker.md`](docs/learn/docker.md).

```powershell
git checkout day-04

# Switching from Day 3: must wipe the volume. `down` without -v leaves ordering.orders
# and the next `dotnet run` fails: relation "orders" already exists.
docker compose down -v
docker rm -f tadka-postgres
docker volume rm tadka_pgdata tadka-cohort_pgdata
docker compose up -d
docker compose ps              # tadka-postgres should be (healthy)

dotnet test Tadka.slnx         # 24/24; needs Docker Desktop
dotnet run --project src/Tadka.Api

curl.exe http://localhost:5224/health
curl.exe http://localhost:5224/health/ready
curl.exe http://localhost:5224/api/v1/restaurants
```

HTTP **5224** (HTTPS 7036). Compose service **`postgres`**. Dev also serves Scalar at `http://localhost:5224/scalar`.

Seed is unchanged from Day 3 (Meghana, biryani ₹299, Priya). Full curls, 201/200, 409, and the locking toy: [`docs/runbooks/day-04.md`](docs/runbooks/day-04.md). Use `curl.exe` and `--data-binary "@docs/runbooks/place-order.json"`.

`Password=tadka_local` in `appsettings.Development.json` is a **local dummy**. Real credentials never go in git.

## Project structure

```
tadka/
├── src/Tadka.Api/
│   ├── Controllers/            # Health, Orders, Restaurants
│   ├── Contracts/
│   ├── Domain/                 # aggregates + OrderFactory + events
│   ├── Data/                   # TadkaDbContext + idempotency store
│   ├── Middleware/             # RFC 7807 + 409
│   └── Migrations/
├── tests/Tadka.Api.Tests/
├── toydemo/day-04-locking/     # FOR UPDATE wait / NOWAIT / SKIP LOCKED (not order API)
├── docs/
│   ├── adrs/                   # 001–013
│   ├── api/                    # OpenAPI 3.1 companion (Day 3 contract)
│   ├── api-contracts.md
│   ├── diagrams/
│   ├── learn/
│   ├── runbooks/
│   └── templates/
└── docker-compose.yml
```

Pricing is still `OrderFactory`. No Payment controller. Pessimistic `FOR UPDATE` is **not** on orders — that demo is the locking toy.

## Architecture Decision Records

| ADR | Decision |
|-----|----------|
| [001](docs/adrs/001-dotnet10.md) | .NET 10 as the runtime |
| [002](docs/adrs/002-monolith-first.md) | Start as a monolith |
| [003](docs/adrs/003-schema-per-domain.md) | One Postgres schema per bounded context |
| [004](docs/adrs/004-ef-core-code-first.md) | EF Core code-first |
| [005](docs/adrs/005-rest-api-style.md) | REST under `/api/v1`; no PUT/DELETE |
| [006](docs/adrs/006-rfc7807-errors.md) | RFC 7807 Problem Details |
| [007](docs/adrs/007-two-layer-validation.md) | FluentValidation + domain rules |
| [008](docs/adrs/008-no-cross-schema-fks.md) | FKs only inside a schema |
| [009](docs/adrs/009-denormalize-order-items.md) | Snapshot name and price on order items |
| [010](docs/adrs/010-api-versioning.md) | URL version `/api/v1` |
| [011](docs/adrs/011-idempotency-for-unsafe-writes.md) | `Idempotency-Key`; unique constraint; 201 then 200 |
| [012](docs/adrs/012-optimistic-concurrency-orders.md) | `xmin` token; loser is **409** |
| [013](docs/adrs/013-in-process-domain-events.md) | Raise fact; dispatch after `SaveChanges` |

## License

Private. For Desi Architect cohort use only.
