# Tadka 🍛

A food delivery platform built as a teaching project for the [Desi Architect](https://desiarchitect.com) cohort.

Tadka starts as a .NET 10 monolith and evolves into **4 services + an API gateway** over 8 weeks. Every architectural decision is earned, not assumed.

> **This is a teaching repo.** The code is *deliberately* over-annotated with inline ADR references and rationale, and several things are hand-rolled (the background payment queue, the Polly pipeline, the HTTP client, object mapping) **to show the mechanics**. Production code should be cleaner, and you would reach for libraries: **MassTransit/NServiceBus** (messaging + outbox), **Microsoft.Extensions.Http.Resilience** (Polly), **Mapster/AutoMapper** (mapping). We expose the wiring for learning; we name the library for Monday morning.

## 🏃 Run it yourself: Student Runbooks

**New here? Start with [`docs/runbooks/`](docs/runbooks/README.md)**: copy-paste guides **per day**. Full platform: `git checkout main` (or `day-16`). Confused why Day 6 ≠ Day 12? Read **[`docs/runbooks/DAY-EVOLUTION.md`](docs/runbooks/DAY-EVOLUTION.md)**. Smoke: `./scripts/cohort-smoke.ps1`.

## Architecture Evolution

| Week | Phase / architecture | What changes |
|---|---|---|
| 1 | Monolith: foundation & domain modeling | One API + PostgreSQL, schema-per-domain, `/health`, domain model, first ADRs (monolith-first). |
| 2 | Monolith: API, state machine & hardening | Full REST API (`/api/v1`), server-side pricing, order state machine, RFC 7807 errors; idempotency, optimistic concurrency, in-process domain events. |
| 3 | Monolith: architect for scale | Indexes + `EXPLAIN`, connection pooling, streaming **read replica** (read/write split); **Redis** cache-aside + stampede lock + CAP; SSE live tracking over Redis backplane. |
| 4 | Modular monolith to first extraction | Payment-gateway brownout to modular monolith, bulkhead/timeouts, CQRS; **extract Payment** (HTTP bridge: why HTTP before Kafka). |
| 5 | Distributed patterns + edge | **Kafka** events, Saga, idempotency, Outbox, DLQ; **API gateway** (YARP), auth (JWT/OAuth2), authz (RBAC/ABAC), OWASP. |
| 6 | 4 services + gateway + deploy topology | Extract **Delivery** + **Restaurant** to final **4 services + an API gateway**; deploy day covers routing and bill, not student Terraform homework. |
| 7 | Production: observability & resilience | OpenTelemetry logs/metrics/traces + SLOs; circuit breakers (Polly), retries/backoff, bulkhead, load shedding, chaos. |
| 8 | Production: case studies & portfolio | Swiggy/Zomato/Razorpay teardowns; k6 load test + cost modeling; partitioning to sharding; final architecture doc + interview pack. |

> **End state: 4 services (Payment, Delivery, Restaurant, and the Ordering/Identity core) plus an API gateway. Never "5 microservices."** The whole point is that you can name the exact failure that earned each one.

> **Pre-built boilerplate:** Each `day-NN` branch has working CRUD, migrations, and handlers. Clone the branch; do not rebuild from scratch. Graded work is ADRs, diagrams, and failure analysis ([`cohort-prep/ASSIGNMENT_RUBRIC.md`](../desiarchitect-website/cohort-prep/ASSIGNMENT_RUBRIC.md)). In class, most time is on architecture, not C# syntax.

## Tech Stack

- **.NET 10**: Web API with Controllers
- **PostgreSQL 16**: Primary database
- **Redis 7**: Caching + live-tracking pub/sub backplane (from Week 3)
- **Apache Kafka**: Event streaming (from Week 5)
- **YARP**: API Gateway (from Week 5)
- **Docker**: Local infrastructure (Postgres; + read replica from Week 3; + Redis from Week 3)
- **EF Core**: ORM with code-first migrations (per-module DbContext from Week 4)
- **MediatR**: In-process events / CQRS-lite (from Week 4)
- **Polly**: Resilience: timeout + bulkhead (Week 4), retries + circuit breaker in Week 7
- **xUnit + Testcontainers**: Unit + integration tests (real PostgreSQL)
- **k6**: Load testing (from Week 3; capstone load test in Week 8)
- **OpenTelemetry + Grafana/Tempo**: Tracing, metrics, logs (from Week 7)
- **Azure Container Apps + ALB / AWS ECS**: Deploy topology in Week 6 and Week 8 (we read routing and cost, not HCL). Infrastructure scripts live in `scripts/` and `deploy/`.

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (4.30+)
- [VS Code](https://code.visualstudio.com/) with C# Dev Kit extension

### Setup

```bash
# Start infrastructure
docker compose up -d

# Verify infrastructure is healthy
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
│   ├── Tadka.Api/           # Monolith (Ordering + Identity)
│   ├── Tadka.Payment.Api/   # Payment Service (:5240, own DB 5434)
│   ├── Tadka.Delivery.Api/  # Delivery Service (:5250, own DB 5435)
│   ├── Tadka.Restaurant.Api/# Restaurant Service (:5260, own DB 5436)
│   └── Tadka.Gateway/       # YARP API Gateway (:8080)
├── tests/                   # Unit and integration tests (207 tests)
├── docs/
│   ├── adrs/                # 66 Architecture Decision Records (001..067)
│   ├── diagrams/            # Mermaid architecture diagrams
│   └── runbooks/            # Day-wise student runbooks (day-01..day-16)
├── docker-compose.yml       # Infrastructure (Postgres, Redis, Kafka, OTel)
├── k6/                      # Load test suite (smoke, average, stress, spike, hot-key)
└── toydemo/                 # 13 failure-first breadth toy demos
```

See [toydemo/README.md](toydemo/README.md) for **13 failure-first toys** (cursor pagination, gRPC vs REST, rate limiters, hot-key stampede, WebSocket backplane, Kafka fan-out, stream windows, search/GIN, HLS+CDN, OLAP/CDC, object storage, web crawler). Each ships `RUN-AND-TEST.md`. Inventory: `toydemo/verify-all.ps1`. Curriculum wiring: [cohort-prep/DEMOS.md](../desiarchitect-website/cohort-prep/DEMOS.md#toy-demos--breadth-topics-tadka-doesnt-build--tadkatoydemo).

## Per-day code states

Each teaching day is a **branch**: check it out and follow its runbook in [`docs/runbooks/`](docs/runbooks/README.md):

```bash
git checkout day-16      # the capstone state (also where all runbooks live)
git checkout day-03      # or any earlier day: day-01 … day-16
```

| Branch | Day | State |
|---|---|---|
| `day-01` | 1 | Scaffold + `/health` |
| `day-02` | 2 | Domain model + schema-per-domain (5 schemas) |
| `day-03` | 3 | Full REST API `/api/v1` (14 endpoints) + order state machine |
| `day-04` | 4 | Hardening: idempotency, optimistic concurrency (409), domain events, integration tests |
| `day-05` | 5 | Indexes + connection pool + streaming read replica (read/write split) |
| `day-06` | 6 | Redis cache-aside + stampede lock + SSE live-tracking backplane |
| `day-07` | 7 | Payment brownout to Polly (timeout + bulkhead), modular monolith + MediatR, async payment |
| `day-08` | 8 | Extract Payment: own process + own DB + HTTP bridge; fault isolation |
| `day-09` | 9 | Kafka + Outbox/Inbox + Saga; consumer catch-up |
| `day-10` | 10 | JWT + RBAC + resource ownership + PII protection |
| `day-11` | 11 | Extract Delivery (3rd service) + YARP gateway + Redis-geo + PgBouncer |
| `day-12` | 12 | Extract Restaurant (4th service) + local read model + zero-downtime backfill |
| `day-13` | 13 | Observability for 4-service system: OpenTelemetry, Jaeger, Prometheus, Grafana |
| `day-14` | 14 | Resilience & Chaos: circuit breaker, retry, Buffer vs Compensate, Redis fall-through |
| `day-15` | 15 | System Design Masterclass: 45-min framework, interview evidence, mock kit, breadth toys |
| `day-16` | 16 | Load testing, cost modeling, Azure cloud deploy, and portfolio capstone |

## Architecture Decision Records

We document every significant technical decision as an ADR in `docs/adrs/`. This is not just for the project. It is a habit every architect should build.

## License

Private. For Desi Architect cohort use only.
