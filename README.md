# Tadka 🍛

A food delivery platform built as a teaching project for the [Desi Architect](https://desiarchitect.com) cohort.

Tadka starts as a .NET 10 monolith and evolves into **4 services + an API gateway** over 8 weeks. Every architectural decision is earned, not assumed.

## 🏃 Run it yourself — Student Runbooks

**New here? Start with [`docs/runbooks/`](docs/runbooks/README.md)** — a copy-paste guide **per day (Day 1 → Day 6)**: how to start the app and infra, every command to run, the API requests to try (with expected responses), and how to verify that day's demo and code actually work. Each day is a git branch (`git checkout day-0N`); the runbooks live on the latest branch (`day-06`).

## Architecture Evolution

| Week | Phase / architecture | What changes |
|------|----------------------|--------------|
| 1 | Monolith — foundation & domain modeling | One API + PostgreSQL, schema-per-domain, `/health`, the domain model, first ADRs (monolith-first). |
| 2 | Monolith — API, state machine & hardening | Full REST API (`/api/v1`), server-side pricing, the order state machine, RFC 7807 errors; idempotency, optimistic concurrency, in-process domain events. |
| 3 | Monolith — architect for scale | Indexes + `EXPLAIN`, connection pooling, a streaming **read replica** (read/write split); **Redis** cache-aside + stampede lock + CAP; SSE live tracking over a Redis backplane. |
| 4 | Modular monolith → first extraction | Payment-gateway brownout → modular monolith, bulkhead/timeouts, CQRS; **extract Payment** (HTTP bridge — why HTTP before Kafka). |
| 5 | Distributed patterns + edge | **Kafka** events, Saga, idempotency, Outbox, DLQ; **API gateway** (YARP), auth (JWT/OAuth2), authz (RBAC/ABAC), OWASP. |
| 6 | 4 services + gateway, on the cloud | Extract **Delivery** + **Restaurant** → the final **4 services + an API gateway**; Docker, AWS ECS, Terraform, CI/CD. |
| 7 | Production — observability & resilience | OpenTelemetry logs/metrics/traces + SLOs; circuit breakers (Polly), retries/backoff, bulkhead, load shedding, chaos. |
| 8 | Production — case studies & portfolio | Swiggy/Zomato/Razorpay teardowns; k6 load test + cost modeling; partitioning → sharding; final architecture doc + interview pack. |

> **End state: 4 services — Payment, Delivery, Restaurant, and the Ordering/Identity core — plus an API gateway. Never "5 microservices."** The whole point is that you can name the exact failure that earned each one.

## Tech Stack

- **.NET 10** — Web API with Controllers
- **PostgreSQL 16** — Primary database
- **Redis 7** — Caching + live-tracking pub/sub backplane (from Week 3)
- **Apache Kafka** — Event streaming (from Week 5)
- **YARP** — API Gateway (from Week 5)
- **Docker** — Local infrastructure (Postgres; + read replica from Week 3; + Redis from Week 3)
- **EF Core** — ORM with code-first migrations
- **xUnit + Testcontainers** — Unit + integration tests (real PostgreSQL)
- **k6** — Load testing (from Week 3; capstone load test in Week 8)
- **OpenTelemetry + Grafana/Tempo** — Tracing, metrics, logs (from Week 7)
- **Terraform + AWS ECS** — Infrastructure as Code + deployment (from Week 6)

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (4.30+)
- [VS Code](https://code.visualstudio.com/) with C# Dev Kit extension

### Setup

```bash
# Start PostgreSQL
docker compose up -d

# Verify PostgreSQL is healthy
docker compose ps

# Run the API
dotnet run --project src/Tadka.Api

# Check health (http 5224, https 7036; API docs at https://localhost:7036/scalar/v2)
curl http://localhost:5224/health
```

> For a full, day-by-day walkthrough (every command + API request + how to verify the demo), follow [`docs/runbooks/`](docs/runbooks/README.md).

### Running Tests

```bash
dotnet test
```

## Project Structure

```
tadka/
├── src/
│   └── Tadka.Api/           # The monolith (becomes modular over time)
│       ├── Controllers/     # API endpoints
│       ├── Domain/          # Business logic, organized by bounded context
│       │   ├── Orders/
│       │   ├── Restaurants/
│       │   ├── Delivery/
│       │   ├── Users/
│       │   └── Payments/
│       └── Data/            # EF Core DbContext and migrations
├── tests/
│   └── Tadka.Api.Tests/     # Unit and integration tests
├── docs/
│   ├── adrs/                # Architecture Decision Records
│   ├── diagrams/            # Mermaid architecture diagrams
│   └── templates/           # Reusable templates
├── docker-compose.yml       # Infrastructure (PostgreSQL, later Redis, Kafka)
└── k6/                      # Load test scripts (from Week 7)
```

## Per-day code states

Each teaching day is a **branch** — check it out and follow its runbook in [`docs/runbooks/`](docs/runbooks/README.md):

```bash
git checkout day-06      # the latest state (also where the runbooks live)
git checkout day-03      # …or any earlier day: day-01 … day-06
```

| Branch | Day | State |
|--------|-----|-------|
| `day-01` | 1 | Scaffold + `/health` |
| `day-02` | 2 | Domain model + schema-per-domain (5 schemas) |
| `day-03` | 3 | Full REST API `/api/v1` (14 endpoints) + order state machine |
| `day-04` | 4 | Hardening: idempotency, optimistic concurrency (409), domain events, integration tests |
| `day-05` | 5 | Indexes + connection pool + streaming read replica (read/write split) |
| `day-06` | 6 | Redis cache-aside + stampede lock + SSE live-tracking backplane |

Days 7–16 (modular monolith → first extraction → **4 services + gateway** → production) land as the cohort progresses. Early **legacy** snapshot tags (`v0.0-scaffold`, `v1.x-monolith-*`, `v2.x-db-and-cache` / `-read-replicas`) predate the day-by-day rebuild and are kept only for reference.

## Architecture Decision Records

We document every significant technical decision as an ADR in `docs/adrs/`. This isn't just for the project. It's a habit every architect should build.

## License

Private. For Desi Architect cohort use only.
