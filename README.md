# Tadka 🍛

A food delivery platform built as a teaching project for the [Desi Architect](https://desiarchitect.com) cohort.

This branch is **Day 3**: a REST API under `/api/v1` on the Day-2 monolith. The client does not send prices. Illegal order transitions return **422**. Errors share one RFC 7807 shape.

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
- **EF Core** — `InitialDomainModel` + `OrderLifecycleAndDemoSeed` on startup
- **FluentValidation** — request validation (400)
- **xUnit** — order state-machine tests
- **Docker Compose** — Postgres only

## Getting started

Prerequisites: [`SETUP.md`](SETUP.md). Compose: [`docs/learn/docker.md`](docs/learn/docker.md).

```bash
git checkout day-03

docker compose down -v
docker compose up -d
docker compose ps              # tadka-postgres should be (healthy)

dotnet run --project src/Tadka.Api

curl.exe http://localhost:5224/health        # liveness — no database field
curl.exe http://localhost:5224/health/ready  # SELECT 1
curl.exe http://localhost:5224/api/v1/restaurants
```

HTTP **5224** (HTTPS 7036). Compose service **`postgres`**. Dev also serves Scalar at `http://localhost:5224/scalar` and OpenAPI at `/openapi/v1.json`.

The seed is 3 Bangalore restaurants, 16 menu items, and customer Priya Sharma. Chicken Biryani is ₹299; two of them total **598.00**. Full curls (PowerShell-safe `--data-binary @file`): [`docs/runbooks/day-03.md`](docs/runbooks/day-03.md). Contract: [`docs/api/openapi-v1.yaml`](docs/api/openapi-v1.yaml).

`Password=tadka_local` in `appsettings.Development.json` is a **local dummy**. Real credentials never go in git.

## Project structure

```
tadka/
├── src/Tadka.Api/
│   ├── Controllers/            # Health, Orders, Restaurants
│   ├── Contracts/              # request/response records + validators
│   ├── Domain/                 # aggregates + OrderFactory + state machine
│   ├── Data/                   # TadkaDbContext + DemoSeed
│   ├── Middleware/             # RFC 7807
│   └── Migrations/
├── tests/Tadka.Api.Tests/
├── docs/
│   ├── adrs/                   # 001–010
│   ├── api/                    # OpenAPI 3.1, why each route, path vs query vs body
│   ├── api-contracts.md        # field-level Markdown companion
│   ├── diagrams/
│   ├── learn/
│   ├── runbooks/
│   └── templates/
└── docker-compose.yml
```

No `IOrderRepository`. Pricing is `OrderFactory`. No Payment controller.

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

## License

Private. For Desi Architect cohort use only.
