# Tadka — Code Repo (cohort capstone)

The .NET 10 codebase students clone and run. This branch is **Day 3**: REST API under `/api/v1` (restaurants + orders), server-side pricing, order state machine, RFC 7807, demo seed. Day 2's schemas and Day 1's liveness `/health` + `/health/ready` stay. Teaching home: `../desi-architect/desiarchitect-website/cohort-prep/`. Public student clone: `D:\work\cohort\tadka-cohort` → `github.com/desiarchitect/tadka-cohort` (do not overlay this private tree onto it).

## Stack (today)

.NET 10 (Controllers) · PostgreSQL 16 · EF Core (`InitialDomainModel` + `OrderLifecycleAndDemoSeed`) · FluentValidation · xUnit. Redis / Kafka / YARP / OTEL / Polly / Terraform / k6 / Testcontainers are later weeks — do not add them on this branch.

## Commands (use the PowerShell tool — Bash/WSL can't see `D:`)

```
dotnet build Tadka.slnx
dotnet test
docker compose down -v
docker compose up -d
dotnet run --project src/Tadka.Api         # migrates + seeds 3 restaurants, 16 items, Priya
curl.exe http://localhost:5224/health      # liveness only
curl.exe http://localhost:5224/health/ready
curl.exe http://localhost:5224/api/v1/restaurants
```

- Compose **service name is `postgres`** (container `tadka-postgres`).
- Switching from Day 2: **`down -v`** or the seed migration hits "relation already exists".
- Student runbook: `docs/runbooks/day-03.md`. Learn guides: `docs/learn/`.

## Layout

`Controllers/OrdersController.cs` and `RestaurantsController.cs` under `/api/v1`. Pricing lives in `Domain/Orders/OrderFactory` (client never sends a price). No `IOrderRepository`. No Payment controller. **Do not create extra `src/` projects.**

## Branches / tags → days

`day-01` = scaffold + liveness `/health` + ADR-001/002. `day-02` = domain model + schemas + `/health/ready` + ADR-003/008. `day-03` = REST + seed + ADR-004–007, 009, 010. Later `day-NN` branches add only what that day earned.

## ADRs (on this branch)

`docs/adrs/`: **001** .NET 10 · **002** monolith-first · **003** schema-per-domain · **004** EF Core code-first · **005** REST API style · **006** RFC 7807 · **007** two-layer validation · **008** no-cross-schema-FKs · **009** denormalize order items · **010** API versioning.

## Gotchas

- **Auto-commit hook** commits with terse messages ("fixed"). Commit explicitly with a real message first.
- Compose creds default to `tadka/tadka_local`.
- Build target is `Tadka.slnx`, not a `.sln`.
- Do not re-introduce empty `k6/`, `terraform/`, `scripts/`, `toydemo/`, or extra service projects.
- `/health` must stay liveness-only. Readiness is `/health/ready`.
