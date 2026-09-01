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
`day-01` ✅ = scaffold + `/health` + ADR-001/002. `day-02` ✅ = domain model + DbContext + schema-per-domain. `day-03` ✅ = full REST API `/api/v1` + order state machine. `day-04` ✅ = idempotency, xmin→409, in-process domain events. `day-05` ✅ = indexes (ADR-014) + pool Min 5/Max 50 (ADR-015) + streaming replica 5433 + `TadkaReadDbContext` (ADR-016) + partition SQL experiment (ADR-017) + `GET /orders/history` keyset (ADR-046). Domain events from Day 4 stay. **Tests: 23 cases** (not 19/19). **No Redis.** HTTP **5224**. Each `day-NN` branches from the previous. Keep `main` intact.

## ADRs (canonical, authoritative numbers)
`docs/adrs/`: 001–013 as on Day 4, plus **014** indexing · **015** pool · **016** replica · **017** partition/shard deferred · **046** keyset cursor on order history. Refer to ADRs by **topic and number** (046 is this branch; later weeks reuse high numbers). Template: `docs/templates/adr-template.md`.

## Gotchas
- **Design-first (Architect's Sequence):** write the ADR — decision + options/trade-offs + failure mode + "Revisit when" — and checkpoint with the user *before* touching a controller. Code is the evidence the design works, not the starting point. (Root `../CLAUDE.md` per-day loop step 0. Slipped on day-02/03 and day-05 — don't repeat it.)
- **Auto-commit hook** commits with terse messages ("fixed"). Commit explicitly with a real message first.
- Compose creds were fixed on `day-01` to default `tadka/tadka_local` matching `appsettings.Development.json` — keep them in sync.
- Build target is `Tadka.slnx` (new SDK solution format), not a `.sln`.
