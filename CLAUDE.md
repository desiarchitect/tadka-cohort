# Tadka — Code Repo (cohort capstone)

The .NET 10 codebase students clone and run. Evolves monolith → 4 services + gateway over 16 days. See the workspace root `../CLAUDE.md` for the cohort-wide orientation and the teaching home (`../desiarchitect-website/cohort-prep/`).

## Stack
.NET 10 (Controllers) · PostgreSQL 16 · EF Core (code-first) · xUnit + FluentAssertions + NSubstitute + Testcontainers. Redis/Kafka/YARP/OTEL/Polly/Terraform enter in later weeks.

## This is a TEACHING repo (read before judging the code)
- **Deliberately over-annotated.** Inline `// (ADR-NNN)` references and rationale paragraphs exist so a student *reading the repo* learns the *why*. Production code should be cleaner — the "why" belongs in ADRs/PRs, not inline. Don't strip the ADR refs (they're the cohort's spine); just know this style is a teaching choice, not a model for prod.
- **Hand-rolled on purpose.** We hand-roll the channel/background processor, the Polly pipeline, the HTTP client, the after-commit event dispatch, and response mapping to *show the mechanics*. **Production libraries (what to use at work)** — tell students this explicitly: messaging + outbox/inbox → **MassTransit / NServiceBus**; HTTP resilience → **Microsoft.Extensions.Http.Resilience** (Polly); object mapping → **Mapster / AutoMapper**; "make dispatch impossible to forget" → an EF **`SaveChanges` interceptor**. We expose the wiring for learning; we name the library for Monday morning.
- **Boundaries are enforced by test, not just convention:** `tests/Tadka.Api.Tests/Architecture/BoundaryTests.cs` fails the build on a cross-schema FK (ADR-008) or any Ordering→Payment reference (ADR-022/024).

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
`day-01` … `day-06` as on day-07 (Redis down → menu 200, SSE 503). `day-07` ✅ = payment brownout Polly + MediatR module + async (ADR-021–023). `day-08` ✅ = extract Payment (`Tadka.Payment.Api` `:5240`, `payment-db` `:5434`): fault/PCI/data isolation not latency (ADR-024); HTTP client + Day-7 Polly (ADR-025); database-per-service (ADR-026). Each `day-NN` branches from the previous. Keep `main`/`week-*` intact.

## ADRs (canonical, authoritative numbers)
`docs/adrs/`: 001 .NET10 · 002 monolith-first · 003 schema-per-domain · 004 ef-core · 005 rest-api · 006 rfc7807-errors · 007 two-layer-validation · 008 no-cross-schema-fks · 009 denormalize-order-items · 010 api-versioning · 011 idempotency-for-unsafe-writes · 012 optimistic-concurrency-orders · 013 in-process-domain-events · 014 indexing-strategy · 015 connection-pool-sizing · 016 read-replica-read-write-split · 017 partitioning-sharding-deferred · 018 redis-cache-aside · 019 cache-stampede-single-flight-lock · 020 live-tracking-sse-redis-backplane · 021 resilient-external-calls-timeout-bulkhead · 022 modular-monolith-mediatr-payment-module · 023 asynchronous-payment-cqrs-lite · 024 extract-payment-service · 025 sync-http-inter-service-bridge · 026 database-per-service. Template: `docs/templates/adr-template.md` (Nygard: Context/Decision/Consequences[+Risks]/Alternatives/References) — must also answer the teaching fields (Topic, Options, Choice, Why, Trade-off, **Failure mode**, **Revisit when**, **Cross-stack equivalents** Java/Spring·Node·Go). ADR titles name the *pattern* (tool in parentheses) — e.g. `021-resilient-external-calls-timeout-bulkhead`, not "polly"; new ADRs fill the template's Cross-stack field so a non-.NET reader maps the decision to their stack. (Cross-stack mapping for already-shipped ADRs lives in the website `cohort-prep/day-NN/option-space.md` — not backfilled into the branched ADRs.)

## Gotchas
- **Design-first (Architect's Sequence):** write the ADR — decision + options/trade-offs + failure mode + "Revisit when" — and checkpoint with the user *before* touching a controller. Code is the evidence the design works, not the starting point. (Root `../CLAUDE.md` per-day loop step 0. Slipped on day-02/03 and day-05 — don't repeat it.)
- **Auto-commit hook** commits with terse messages ("fixed"). Commit explicitly with a real message first.
- Compose creds were fixed on `day-01` to default `tadka/tadka_local` matching `appsettings.Development.json` — keep them in sync.
- Build target is `Tadka.slnx` (new SDK solution format), not a `.sln`.
