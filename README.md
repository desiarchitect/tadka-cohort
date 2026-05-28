# Tadka 🍛

A food delivery platform built as a teaching project for the [Desi Architect](https://desiarchitect.com) cohort.

Tadka starts as a .NET 10 monolith and evolves into a microservices architecture over 8 weeks. Every architectural decision is earned, not assumed.

## Architecture Evolution

| Week | Architecture | What Changes |
|------|-------------|-------------|
| 1 | Monolith | Single API + PostgreSQL. CRUD, health check, domain folders. |
| 2 | Monolith + Caching | Redis for menu/restaurant caching. Read replicas for PostgreSQL. |
| 3 | Monolith + Async | Kafka for order events. Async processing for non-critical paths. |
| 4 | Modular Monolith → First Extraction | CQRS pattern. Order service extracted as first microservice. |
| 5 | 2 Services | API Gateway (YARP). Distributed transactions (Saga). |
| 6 | 3 Services | Observability (OpenTelemetry). Circuit breakers. Chaos engineering. |
| 7 | 4 Services | CDN. Rate limiting. Capacity planning with k6 load tests. |
| 8 | 5 Services | Final architecture. Real-world teardowns. Production readiness. |

## Tech Stack

- **.NET 10** — Web API with Controllers
- **PostgreSQL 16** — Primary database
- **Redis 7** — Caching (from Week 2)
- **Apache Kafka** — Event streaming (from Week 3)
- **YARP** — API Gateway (from Week 5)
- **Docker** — Local infrastructure
- **EF Core** — ORM with code-first migrations
- **xUnit + FluentAssertions + NSubstitute** — Testing
- **Testcontainers** — Integration tests
- **OpenTelemetry** — Distributed tracing and metrics (from Week 6)
- **k6** — Load testing (from Week 7)
- **Terraform** — Infrastructure as Code (from Week 8)

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

# Check health
curl http://localhost:5000/health
```

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

## Architecture Decision Records

We document every significant technical decision as an ADR in `docs/adrs/`. This isn't just for the project. It's a habit every architect should build.

## License

Private. For Desi Architect cohort use only.
