# Tadka 🍛

A food delivery platform, built one earned decision at a time.

This is the code you write during the [Desi Architect](https://desiarchitect.com) cohort. Tadka starts as a single .NET monolith with one endpoint. Over 16 sessions it grows into four services behind a gateway, with an event backbone, a cache, and full observability.

Nothing here was added because it was fashionable. Every component arrives only after you have watched the system break without it.

## How this repo works

**One branch per day.** Each day of the cohort has its own branch, and each branch is the exact state of the codebase at the end of that session.

```bash
git checkout day-01     # where we start
git checkout day-02     # after the domain model lands
```

**Days unlock as the cohort moves.** You will only see branches for the days that have already run. If `day-05` is not there yet, that is on purpose, not a bug. Run `git fetch` before each session to pull the newest one.

```bash
git fetch                 # pull down newly released days
git branch -r             # see what is available
```

## Quick start

This branch is **Day 2**: one API, one PostgreSQL database, five bounded contexts in folders, each mapped to its own Postgres **schema**. HTTP business APIs are not here yet (Day 3).

```bash
git clone https://github.com/desiarchitect/tadka-cohort.git tadka
cd tadka
git checkout day-02

# Fresh volume — a leftover Day 1 database will fail the migration
docker compose down -v
docker compose up -d
docker compose ps                         # wait for tadka-postgres to say (healthy)

dotnet run --project src/Tadka.Api        # applies InitialDomainModel on startup
```

Then, in a second terminal:

```bash
curl.exe http://localhost:5224/health
curl.exe http://localhost:5224/health/ready
```

`/health` is liveness — process up, **no** `database` field. `/health/ready` talks to Postgres (`SELECT 1`) and should return Connected.

On Windows PowerShell, `curl` is an alias for `Invoke-WebRequest`. Use **`curl.exe`**.

HTTP listens on **5224** (HTTPS 7036). Compose service name is **`postgres`** (container `tadka-postgres`).

```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "\dn"
```

You should see five schemas: `ordering`, `restaurant`, `delivery`, `identity`, `payment`.

Full command sequence: [`docs/runbooks/day-02.md`](docs/runbooks/day-02.md).

`Password=tadka_local` in `appsettings.Development.json` is a **local dummy**. Real credentials never go in git.

## Connection facts

These stay the same on every branch.

| | |
|---|---|
| API | `http://localhost:5224` (https `7036`) |
| Postgres | `localhost:5432`, database `tadka`, user `tadka`, password `tadka_local` |
| Compose service name | `postgres` (container `tadka-postgres`) |

## Common commands

```bash
dotnet build Tadka.slnx
dotnet test
docker compose down -v && docker compose up -d    # full database reset
```

Use the reset when the migration fails with `relation already exists`.

## Where this is going

Over 8 weeks this monolith evolves into **four services plus a gateway**. Destination: [`docs/diagrams/day-01-final-architecture.md`](docs/diagrams/day-01-final-architecture.md).

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

## Project structure

```
tadka/
├── src/Tadka.Api/
│   ├── Controllers/            # Health + Ready only
│   ├── Domain/
│   │   ├── Orders/
│   │   ├── Restaurants/
│   │   ├── Delivery/
│   │   ├── Users/              # maps to schema identity
│   │   ├── Payments/
│   │   └── ValueObjects/       # Money, Address, GeoLocation
│   ├── Data/
│   └── Migrations/             # InitialDomainModel
├── tests/Tadka.Api.Tests/
├── docs/
│   ├── adrs/                   # 001, 002, 003, 008
│   ├── diagrams/
│   ├── learn/
│   ├── runbooks/
│   └── templates/
└── docker-compose.yml
```

Folder `Users` / schema `identity` is intentional (aggregate name vs bounded-context name).

## Architecture Decision Records

| ADR | Decision |
|-----|----------|
| [001](docs/adrs/001-dotnet10.md) | .NET 10 as the runtime |
| [002](docs/adrs/002-monolith-first.md) | Start as a monolith |
| [003](docs/adrs/003-schema-per-domain.md) | One Postgres schema per bounded context |
| [008](docs/adrs/008-no-cross-schema-fks.md) | FKs only inside a schema; cross-domain refs by id |

## Getting help

Post in `#doubts` on Discord with the command you ran and the full error text.

## License

Copyright © Desi Architect. All rights reserved.

This repository is published so cohort participants can clone it easily. It is course material, not open source. You are welcome to read it, run it, and learn from it. Please do not redistribute it or use it to teach a competing course.
