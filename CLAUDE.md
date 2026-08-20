# Tadka — Code Repo (cohort capstone)

The .NET 10 codebase students clone and run. This branch is **Day 1**: monolith scaffold + `/health` + ADR-001/002. It evolves into 4 services + gateway over 16 days. Later `day-NN` branches unlock as the cohort moves.

## Stack (today)

.NET 10 (Controllers) · PostgreSQL 16 · EF Core (empty `TadkaDbContext`) · xUnit. Redis / Kafka / YARP / OTEL / Polly / Terraform / k6 are later weeks — do not add them on this branch.

## Commands (use the PowerShell tool — Bash/WSL can't see `D:`)

```
dotnet build Tadka.slnx
dotnet test
docker compose up -d                       # starts postgres (creds default to tadka/tadka_local — no .env needed)
dotnet run --project src/Tadka.Api
curl http://localhost:5224/health          # http 5224, https 7036 — liveness only, no DB check
```

- Compose **service name is `postgres`** (container `tadka-postgres`). Use `docker compose stop postgres` in the Copilot readiness demo.
- `/health` is process-up only. The Day 1 Copilot demo adds `/health/ready`, which hits Postgres via `TadkaDbContext`. That first ready-hit is ~500–800ms (EF/Npgsql warm-up); steady state is single-digit ms.
- Student demo runbook (commands + expected output): `docs/runbooks/day-01.md`.
- Student learn guides: `docs/learn/docker.md` (Compose commands) and `docs/learn/ai-context-files.md` (CLAUDE.md / Copilot / `.github` / `.claude` catalog). Do not scaffold empty `.claude/` or `.github/skills` on Day 1.

## Layout

`src/Tadka.Api` has `Controllers/` (liveness `/health`) and `Data/` (empty `TadkaDbContext`). **No `Domain/` folder yet** — bounded contexts are named on Day 2. **Do not create extra `src/` projects** (Payment.Api, Gateway, …) and do not pre-create `Domain/Orders` etc. on this branch.

## Branches / tags → days

`day-01` = scaffold + liveness `/health` + ADR-001/002. Later `day-NN` branches add only what that day earned. This public repo does not carry `main`.

## ADRs (on this branch)

`docs/adrs/`: **001** .NET 10 · **002** monolith-first. Template: `docs/templates/adr-template.md` (Nygard: Context/Decision/Consequences[+Risks]/Alternatives/References) — must also answer the 7 teaching fields (Topic, Options, Choice, Why, Trade-off, **Failure mode**, **Revisit when**). Later ADR numbers (003+) belong on later day branches.

## Gotchas

- **Auto-commit hook** commits with terse messages ("fixed"). Commit explicitly with a real message first.
- Compose creds default to `tadka/tadka_local`, matching `appsettings.Development.json` — keep them in sync.
- Build target is `Tadka.slnx` (new SDK solution format), not a `.sln`.
- Do not re-introduce empty `k6/`, `terraform/`, `scripts/`, `toydemo/`, `Domain/` folders, or extra service projects on Day 1. Add a folder the day it is first used.
