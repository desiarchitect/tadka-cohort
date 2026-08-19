# Tadka 🍛

A food delivery platform built as a teaching project for the [Desi Architect](https://desiarchitect.com) cohort.

This branch is **Day 2**: one .NET 10 API, one PostgreSQL database, five bounded contexts in folders, each mapped to its own Postgres **schema**. HTTP business APIs are not here yet (Day 3).

## Where this is going

Over 8 weeks this monolith evolves into 4 services plus a gateway. You earn every box. Destination: [`docs/diagrams/day-01-final-architecture.md`](docs/diagrams/day-01-final-architecture.md).

Right now:

```
Client  →  Tadka.Api  →  PostgreSQL 16
                         ├── ordering
                         ├── restaurant
                         ├── delivery
                         ├── identity
                         └── payment
```

No arrows between those schemas. That absence is ADR-008.

## Tech stack (today)

- **.NET 10** — Web API with Controllers
- **PostgreSQL 16** — one database, five schemas
- **EF Core** — `InitialDomainModel` applied on startup
- **xUnit** — so `dotnet test` runs
- **Docker Compose** — Postgres only

## Getting started

Prerequisites and installs: [`SETUP.md`](SETUP.md). Compose commands: [`docs/learn/docker.md`](docs/learn/docker.md).

```bash
git checkout day-02

# Fresh volume — a leftover Day 1 DB will fail the migration
docker compose down -v
docker compose up -d
docker compose ps              # tadka-postgres should be (healthy)

dotnet run --project src/Tadka.Api

curl.exe http://localhost:5224/health        # liveness — no database field
curl.exe http://localhost:5224/health/ready  # SELECT 1 — 200 once migrate finished
```

HTTP **5224** (HTTPS 7036). Compose service **`postgres`**.

Full command sequence and `\dn` payoff: [`docs/runbooks/day-02.md`](docs/runbooks/day-02.md).

`Password=tadka_local` in `appsettings.Development.json` is a **local dummy**. Real credentials never go in git.

## Project structure

```
tadka/
├── src/Tadka.Api/
│   ├── Controllers/            # Health + Ready only
│   ├── Domain/
│   │   ├── Orders/             # Order aggregate
│   │   ├── Restaurants/
│   │   ├── Delivery/
│   │   ├── Users/              # maps to schema identity
│   │   ├── Payments/
│   │   └── ValueObjects/       # Money, Address, GeoLocation
│   ├── Data/                   # TadkaDbContext — ToTable(..., "schema")
│   └── Migrations/             # InitialDomainModel
├── tests/Tadka.Api.Tests/
├── docs/
│   ├── adrs/                   # 001, 002, 003, 008
│   ├── diagrams/
│   ├── learn/
│   ├── runbooks/
│   └── templates/
└── docker-compose.yml          # PostgreSQL only
```

The domain folders are the Day 2 decision, not empty microservice projects.

## Architecture Decision Records

| ADR | Decision |
|-----|----------|
| [001](docs/adrs/001-dotnet10.md) | .NET 10 as the runtime |
| [002](docs/adrs/002-monolith-first.md) | Start as a monolith |
| [003](docs/adrs/003-schema-per-domain.md) | One Postgres schema per bounded context |
| [008](docs/adrs/008-no-cross-schema-fks.md) | FKs only inside a schema; cross-domain refs by id |

## License

Private. For Desi Architect cohort use only.
