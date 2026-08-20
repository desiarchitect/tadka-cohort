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

Reading ahead is not blocked because we do not trust you. It is that the whole course is built on feeling a problem before you see its solution. Day 8 ends with a payment that vanishes silently. If you have already read the Day 9 fix, that moment teaches you nothing.

## Before Day 1

Install these and check them before the first session. Budget 30 to 45 minutes. Full walkthrough with troubleshooting is in [`SETUP.md`](SETUP.md).

| Tool | Version | Check |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | 10.x | `dotnet --version` |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | 4.30+ | `docker compose version` |
| [Git](https://git-scm.com/downloads) | any recent | `git --version` |

An editor with C# support helps. VS Code with the C# Dev Kit extension, or Rider, or Visual Studio.

On Windows, turn on the WSL 2 backend in Docker Desktop. On Mac, give Docker at least 4 GB of RAM.

## Quick start

```bash
git clone https://github.com/desiarchitect/tadka-cohort.git tadka
cd tadka
git checkout day-01

docker compose up -d                      # starts PostgreSQL
docker compose ps                         # wait for tadka-postgres to say (healthy)

dotnet run --project src/Tadka.Api        # builds, migrates, starts the API
```

Then, in a second terminal:

```bash
curl http://localhost:5224/health
```

You should get back something like this:

```json
{ "status": "Healthy", "database": "Connected", "responseTime": "52ms" }
```

The first call is slow (roughly half a second) and every call after it is fast. That gap is not a bug, and on Day 1 we spend real time on why it happens.

To explore the API in a browser, open `https://localhost:7036/scalar/v2`.

## Connection facts

These stay the same on every branch.

| | |
|---|---|
| API | `http://localhost:5224` (https `7036`) |
| Postgres | `localhost:5432`, database `tadka`, user `tadka`, password `tadka_local` |
| Compose service name | `postgres` (container `tadka-postgres`) |

Credentials are local development values with no secrets in them, which is why they are committed.

## Common commands

```bash
dotnet build Tadka.slnx                   # build everything
dotnet test                               # run tests (Day 4 onward, needs Docker)

docker compose down -v && docker compose up -d    # full database reset
```

Use the reset when a migration fails with `relation already exists`. It wipes the volume and starts clean.

## The 16 days

Two sessions a week, over eight weeks.

| Week | Days | What you build |
|---|---|---|
| 1 | 1, 2 | The monolith and its domain model. Bounded contexts, schema-per-domain. |
| 2 | 3, 4 | A real REST API, then hardening it against retries and races. |
| 3 | 5, 6 | Making the database fast, then caching and live order tracking. |
| 4 | 7, 8 | A payment brownout, and extracting Payment into its own service. |
| 5 | 9, 10 | A durable event backbone, then securing the whole system. |
| 6 | 11, 12 | Extracting Delivery and Restaurant. Four services behind a gateway. |
| 7 | 13, 14 | Making the system observable, then deliberately breaking it. |
| 8 | 15, 16 | Interview teardowns, load testing to the breaking point, and cost. |

The end state is **four services plus a gateway**: Ordering, Payment, Delivery, Restaurant. Identity lives inside Ordering, because it never earned an extraction of its own.

## Tech stack

Each tool enters in the week you feel the need for it, not before.

| | Enters | Why |
|---|---|---|
| .NET 10, PostgreSQL 16 | Week 1 | The starting monolith |
| EF Core | Week 1 | Code-first migrations |
| xUnit, Testcontainers | Week 2 | Tests against a real database |
| Redis | Week 3 | Caching, then locks and geo |
| Polly | Week 4 | Timeouts and bulkheads around a slow gateway |
| Kafka | Week 5 | A durable log so messages survive a service being down |
| YARP | Week 6 | One entry point once there are several services |
| OpenTelemetry, Serilog | Week 7 | A choreographed flow is invisible without tracing |
| k6, Terraform | Week 8 | Load to the breaking point, and the bill |

If you work in Java, Node or Go, you are in the right place. The assignments are ADRs, diagrams and failure analysis, none of which are language specific. Each day's notes map the .NET pieces to your stack.

## Architecture Decision Records

Every significant decision is written down in [`docs/adrs/`](docs/adrs/), with the options considered, the trade-off accepted, how it could fail, and what would make us revisit it.

The ADRs grow with the branches. On `day-01` there are two. By the end there are forty-four.

This is the habit the cohort is really trying to build. The code tells you what the system does. The ADR tells you why, six months later, when nobody remembers.

## Getting help

Post in `#doubts` on Discord with the command you ran and the full error text. A screenshot of the terminal is fine.

## License

Copyright © Desi Architect. All rights reserved.

This repository is published so cohort participants can clone it easily. It is course material, not open source. You are welcome to read it, run it, and learn from it. Please do not redistribute it or use it to teach a competing course.
