# Tadka — Code Repo (cohort capstone)

The .NET 10 codebase students clone and run. Evolves monolith → 4 services + gateway over 16 days. See the workspace root `../CLAUDE.md` for the cohort-wide orientation and the teaching home (`../desiarchitect-website/cohort-prep/`).

## Stack
.NET 10 (Controllers) · PostgreSQL 16 · EF Core (code-first) · xUnit + FluentAssertions + NSubstitute + Testcontainers. Redis/Kafka/YARP/OTEL/Polly/Terraform enter in later weeks.

## Commands (use the PowerShell tool — Bash/WSL can't see `D:`)
```
dotnet build Tadka.slnx
dotnet test
docker compose up -d                       # starts postgres (creds default to tadka/tadka_local — no .env needed)
dotnet run --project src/Tadka.Api
curl http://localhost:5224/health          # http 5224, https 7036
```
- Compose **service name is `postgres`** (container `tadka-postgres`). Use `docker compose stop postgres` in demos.
- First `/health` hit is ~700ms (EF/Npgsql warm-up); steady state is single-digit ms.

## Layout & schema-per-domain
`src/Tadka.Api/Domain/{Orders,Restaurants,Delivery,Users,Payments}` — folders are future service boundaries. Postgres schemas: `ordering, restaurant, delivery, identity, payment`. **No cross-schema FKs** (ADR-008); cross-domain refs by ID only. Value objects (Money, Address, GeoLocation) as C# records via EF `OwnsOne`.

## Branches / tags → days
`day-01` ✅ = scaffold + `/health` + ADR-001/002. `day-02` (to create) = domain model + DbContext + schema. Tags: `v0.0-scaffold`(D0) → `v0.1.1-day01-fix`(D1) → `v1.0/v1.1-monolith-*`(D3–4) → `v2.x`(D5–6). `week-3` holds WIP spine (growth-story, break-kits, k6). Keep `main`/`week-*` intact.

## ADRs (canonical, authoritative numbers)
`docs/adrs/`: 001 .NET10 · 002 monolith-first · 003 schema-per-domain · 004 ef-core · 005 rest-api · 006 rfc7807-errors · 007 two-layer-validation · 008 no-cross-schema-fks · 009 denormalize-order-items. Template: `docs/templates/adr-template.md` (Nygard: Context/Decision/Consequences[+Risks]/Alternatives/References) — must also answer the 7 teaching fields (Topic, Options, Choice, Why, Trade-off, **Failure mode**, **Revisit when**).

## Gotchas
- **Auto-commit hook** commits with terse messages ("fixed"). Commit explicitly with a real message first.
- Compose creds were fixed on `day-01` to default `tadka/tadka_local` matching `appsettings.Development.json` — keep them in sync.
- Build target is `Tadka.slnx` (new SDK solution format), not a `.sln`.
