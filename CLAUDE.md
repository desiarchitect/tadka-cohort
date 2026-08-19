# Tadka — Code Repo (cohort capstone)

The .NET 10 codebase students clone and run. This branch is **Day 2**: domain model + schema-per-domain + ADR-003/008, plus Day 1's liveness `/health` and Copilot-added `/health/ready`. Teaching home: `../desi-architect/desiarchitect-website/cohort-prep/`.

## Stack (today)

.NET 10 (Controllers) · PostgreSQL 16 · EF Core code-first (`InitialDomainModel` on startup) · xUnit. Redis / Kafka / YARP / OTEL / Polly / Terraform / k6 are later weeks — do not add them on this branch.

## Commands (use the PowerShell tool — Bash/WSL can't see `D:`)

```
dotnet build Tadka.slnx
dotnet test
docker compose down -v
docker compose up -d                       # creds tadka/tadka_local — down -v first if switching onto this branch
dotnet run --project src/Tadka.Api         # applies InitialDomainModel
curl http://localhost:5224/health          # liveness only
curl http://localhost:5224/health/ready    # SELECT 1 against Postgres
docker exec tadka-postgres psql -U tadka -d tadka -c "\dn"
```

- Compose **service name is `postgres`** (container `tadka-postgres`).
- First `/health/ready` after `dotnet run` is ~500–800ms (EF/Npgsql warm-up); steady state is single-digit ms.
- Student runbook: `docs/runbooks/day-02.md`. Learn guides: `docs/learn/`.

## Layout & schema-per-domain

`src/Tadka.Api/Domain/{Orders,Restaurants,Delivery,Users,Payments}` plus `ValueObjects`. Postgres schemas: `ordering, restaurant, delivery, identity, payment`. Folder `Users` / schema `identity` is intentional. **No cross-schema FKs** (ADR-008); cross-domain refs by ID only. Value objects as C# records via EF `OwnsOne`.

**Do not create extra `src/` projects** on this branch.

## Branches / tags → days

`day-01` = scaffold + liveness `/health` + ADR-001/002. `day-02` = domain model + schemas + `/health/ready` + ADR-003/008. Later `day-NN` branches add only what that day earned.

## ADRs (on this branch)

`docs/adrs/`: **001** .NET 10 · **002** monolith-first · **003** schema-per-domain · **008** no-cross-schema-FKs. Template: `docs/templates/adr-template.md`. Later numbers (004+) belong on later day branches.

## Gotchas

- **Auto-commit hook** commits with terse messages ("fixed"). Commit explicitly with a real message first.
- Switching onto this branch: `docker compose down -v` or the migration hits "relation already exists".
- Compose creds default to `tadka/tadka_local`, matching `appsettings.Development.json`.
- Build target is `Tadka.slnx`, not a `.sln`.
- Do not re-introduce empty `k6/`, `terraform/`, `scripts/`, `toydemo/`, or extra service projects.
