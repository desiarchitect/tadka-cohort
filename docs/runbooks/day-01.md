# Day 1 — Runbook: Scaffold + `/health`

**Branch:** `day-01` · **What exists today:** one .NET 10 API, one PostgreSQL database, a liveness `/health` that does **not** talk to Postgres. Nothing else — and that is the point.

Compose commands and service vs container name: [`docs/learn/docker.md`](../learn/docker.md). Copilot / Claude context files (including skills and agents you can add later): [`docs/learn/ai-context-files.md`](../learn/ai-context-files.md).

> **Windows PowerShell:** `curl` is an alias for `Invoke-WebRequest`. Use **`curl.exe`** everywhere below (or Git Bash / WSL). Ports and compose names are the same on every OS.

| Thing | Value |
|-------|--------|
| API (http) | `http://localhost:5224` |
| API (https) | `https://localhost:7036` |
| Compose service | `postgres` (container `tadka-postgres`) |
| Postgres | `localhost:5432`, db `tadka`, user `tadka`, password `tadka_local` |

### pgAdmin (or any GUI) from your machine

This is **Postgres on localhost**, not SQL Server LocalDB. pgAdmin talks to the **mapped port**, not the container name.

Register a server:

| Field | Value |
|-------|--------|
| Host | `localhost` or `127.0.0.1` |
| Port | `5432` |
| Maintenance database | `tadka` |
| Username | `tadka` |
| Password | `tadka_local` |

Same as `appsettings.Development.json`. Container must be **healthy** (`docker compose ps`).

**Do not** use Host `tadka-postgres` — that name only works inside Docker.

If connect fails with “already in use” / wrong database: a **local** Postgres install may already own `5432`. Stop that Windows service, or you are not hitting the Docker instance. `docker compose ps` PORTS must show `0.0.0.0:5432->5432`.

Day 1: you will mostly see `public`. Five domain schemas (`ordering`, …) appear on Day 2 after `InitialDomainModel`.

### Docker — enough to run this runbook

You do not install Postgres on Windows. **Docker Desktop** runs an official Postgres 16 image so every laptop looks the same. Install Docker Desktop, start it, then run every command below from the **repo root** (the folder that contains `docker-compose.yml`).

| Word | Meaning here |
|------|----------------|
| **Image** | The recipe. `postgres:16` is downloaded once from Docker Hub. |
| **Container** | A running copy of that image. Ours is named `tadka-postgres`. |
| **Compose** | Reads `docker-compose.yml` and starts what this day needs. Use `docker compose` (space), not the old `docker-compose` binary. |

Two names you must not mix up:

| Name | What you type it with |
|------|------------------------|
| **Service** `postgres` | Compose: `up`, `stop`, `start`, `logs` |
| **Container** `tadka-postgres` | `docker exec`, `docker ps` |

`docker compose stop db` fails. There is no service named `db`.

**Every Docker command in this file**

| Command | What it does |
|---------|----------------|
| `docker compose up -d` | Create and start the Postgres container **in the background** (`-d` = detached: this terminal stays free). First run downloads `postgres:16`. |
| `docker compose ps` | List this project's containers. You want `tadka-postgres` … **`(healthy)`**. `starting` / `unhealthy`: wait ~10 s and run it again. |
| `docker exec tadka-postgres pg_isready -U tadka` | Run `pg_isready` **inside** the container, as user `tadka`. “Accepting connections” means Postgres can take clients. It does **not** list tables. |
| `docker compose stop postgres` | Stop the DB. Container (and data) stay on disk. The API process keeps running so you can see `/health` vs `/health/ready`. |
| `docker compose start postgres` | Start that same container again. Data is still there. Wait for `(healthy)` before curling ready. |
| `docker compose logs postgres` | Print Postgres logs. Use the **service** name. Last lines usually show password / port / data-dir errors. |
| `docker compose down -v` | Stop **and remove** the container, network, **and the named volume**. Database is empty after this. Day 1 has no seed to lose. |

`stop` / `start` = pause and resume. `down` = tear down. `-v` = also wipe stored data.

**“container name already in use”:** another folder (for example private `tadka` vs `tadka-cohort`) already started a container named `tadka-postgres`. From **that** folder run `docker compose down`, then `up -d` here. Only one container can use that name and port `5432`.

### Local dummy password — not a production secret

`appsettings.Development.json` contains `Password=tadka_local`. That is **committed on purpose**: it is a throwaway local Docker database with no real data. `appsettings.json` has no connection string.

ASP.NET Core config is layered (later wins):

```
appsettings.json                     ← no secrets
appsettings.Development.json         ← local dummy, in git, throwaway DB
User Secrets (Development only)      ← machine-local, not in git
Environment variables                ← how production actually injects it
```

Optional override (does **not** change student setup). In PowerShell, before `dotnet run`:

```powershell
$env:ConnectionStrings__TadkaDb = "Host=localhost;Port=5432;Database=tadka;Username=tadka;Password=tadka_local"
```

When the password is real it never lives in git — User Secrets locally, env / secret store in deploy (Day 10). Do not copy this Development file into production config.

---

## 0. What the tree should look like (before you run anything)

```bash
git checkout day-01
dotnet build Tadka.slnx
```

**Look for**

- Build: **0 errors, 0 warnings**.
- Solution has **two projects only:** `src/Tadka.Api` and `tests/Tadka.Api.Tests`. No Payment / Delivery / Gateway projects.
- `src/Tadka.Api` has `Controllers/` and `Data/` only. **No `Domain/` folder** — bounded contexts are a Day 2 design, not a Day 1 scaffold.
- `docker-compose.yml` starts **Postgres only**. No Redis, no Kafka.
- `GET /health` in `HealthController` returns `status` + `timestamp` only — no `database` field yet.
- `appsettings.json` has **no** connection string. The dummy password is only in `appsettings.Development.json`, with a comment that it is local-only.

---

## 1. Start Postgres

From the repo root. `-d` keeps this terminal free.

```bash
docker compose up -d
docker compose ps
```

**Look for**

```
NAME             IMAGE         STATUS
tadka-postgres   postgres:16   Up ... (healthy)
```

If status is `starting` or `unhealthy`, wait ~10 seconds and run `docker compose ps` again.

Quick probe **inside** the container (`exec` = run this one command in `tadka-postgres`):

```bash
docker exec tadka-postgres pg_isready -U tadka
# → localhost:5432 - accepting connections
```

---

## 2. Run the API

Keep this terminal open.

```bash
dotnet run --project src/Tadka.Api
```

**Look for**

```
Now listening on: http://localhost:5224
Application started.
```

There is **no migration** on Day 1. The DbContext is empty. If you see EF applying migrations, you are on the wrong branch.

---

## 3. Baseline — liveness only

In a **second** terminal, from the repo root:

```bash
curl.exe -s -w "\nHTTP %{http_code}\n" http://localhost:5224/health
```

**Look for — HTTP 200**

```json
{ "status": "Healthy", "timestamp": "2026-..." }
```

What must **not** be there: `database`, `responseTime`. This endpoint only means “the process is up.” A load balancer that trusts this while Postgres is down is being lied to. That is the setup for the next step.

Optional: Scalar UI at `https://localhost:7036/scalar` (accept the dev cert if the browser warns).

---

## 4. Live build — add `/health/ready`

This is the Day 1 Copilot demo. Open the repo in VS Code, open Copilot Chat in **Agent Mode**, and paste this prompt **verbatim**:

```
Add a /health/ready endpoint to HealthController that checks PostgreSQL connectivity.
It should attempt a simple query, measure the response time, and return:
- database status (healthy/unhealthy)
- response time in milliseconds
- overall status based on all checks

Use the existing TadkaDbContext. Return 200 if healthy, 503 if any check fails.
```

**Look for in the generated code**

- It **injects** the existing `TadkaDbContext` — it does not open a new `NpgsqlConnection`.
- `async`/`await`, no `.Result` / `.Wait()`.
- A **new** action (`GET /health/ready`), not a rewrite of `GET /health`.
- Controllers style (not Minimal APIs) — because `.github/copilot-instructions.md` says so.

If Copilot is down, add this by hand to `HealthController` (constructor + `Ready()`), then continue. The break/fix below is the real lesson; Copilot is the generator.

Restart the API (`Ctrl+C` in the run terminal, then `dotnet run --project src/Tadka.Api` again) so the new action is loaded.

---

## 5. Ready check — Postgres up

```bash
curl.exe -s -w "\nHTTP %{http_code}\n" http://localhost:5224/health/ready
```

**Look for — HTTP 200**

Something in this shape (field names may vary slightly):

```json
{
  "status": "healthy",
  "timestamp": "...",
  "checks": {
    "database": { "status": "healthy", "responseTimeMs": 3 }
  }
}
```

**Cold start:** the **first** `/health/ready` after `dotnet run` is often **500–800 ms** (EF model build + Npgsql pool + JIT). Hit it again. Steady state should be **single-digit ms**. That is why we measure p99 on a warm system, not the first request.

Leave `/health` alone and hit it too — it must still be the simple liveness payload from step 3.

---

## 6. Break — stop Postgres

Compose **service** name is **`postgres`**, not `db`. This stops the database only; it does not kill `dotnet run`.

```bash
docker compose stop postgres
curl.exe -s -w "\nHTTP %{http_code}\n" http://localhost:5224/health/ready
curl.exe -s -w "\nHTTP %{http_code}\n" http://localhost:5224/health
```

**Look for**

| Endpoint | HTTP | Body |
|----------|------|------|
| `/health/ready` | **503** | `"unhealthy"` / `"Disconnected"` (or similar) plus a response time |
| `/health` | **200** | `{ "status": "Healthy", "timestamp": "..." }` — **no database field** |

That split is the whole demo: process-up is not the same as system-healthy.

---

## 7. Fix — start Postgres

`start` resumes the same container (data still there). Wait until `ps` shows `(healthy)`.

```bash
docker compose start postgres
docker compose ps
# wait until tadka-postgres is (healthy), then:
curl.exe -s -w "\nHTTP %{http_code}\n" http://localhost:5224/health/ready
```

**Look for** — `/health/ready` is **200** again, database connected, single-digit ms after the first hit.

---

## 8. Tests (optional)

```bash
dotnet test Tadka.slnx
```

**Look for** — 1 passing placeholder test. Day 1 does not have health integration tests yet.

---

## Done when

- [ ] `dotnet build Tadka.slnx` is clean (0 errors).
- [ ] `docker compose ps` shows `tadka-postgres` **healthy**.
- [ ] `GET /health` → **200**, `"Healthy"`, **no** `database` field.
- [ ] Copilot (or the fallback) added `GET /health/ready` that uses `TadkaDbContext`.
- [ ] First `/health/ready` may be hundreds of ms; the next one is single-digit ms.
- [ ] `docker compose stop postgres` → `/health/ready` **503**, `/health` still **200**.
- [ ] `docker compose start postgres` → `/health/ready` **200** again.

---

## Troubleshooting

| Symptom | What to do |
|---------|------------|
| `tadka-postgres` not healthy | `docker compose logs postgres` (service name). Wait 10 s. If port 5432 is taken, stop the other Postgres. |
| Container name already in use | Another compose project owns `tadka-postgres`. `docker compose down` in that folder, then `up -d` here. |
| `Failed to bind ... 5224` | A previous `dotnet run` is still up. Stop it, or `Get-NetTCPConnection -LocalPort 5224` and kill that PID. |
| `curl` prints a huge HTML / method error | You used PowerShell `curl`. Switch to `curl.exe`. |
| `docker compose stop db` fails | Service is `postgres`. |
| `/health` already returns `database` / `Connected` | You are not on the Day 1 scaffold (or you already applied the ready check to `/health`). `GET /health` must stay liveness-only. |
| `/health/ready` 404 | API was not restarted after Copilot edited the controller. |
| Ready stays 503 after `start postgres` | Container is up but not healthy yet. `docker compose ps` until `(healthy)`, then retry. |
| Copilot writes Minimal APIs | Point at `.github/copilot-instructions.md` (“Use Controllers”) and re-prompt. Good live teaching moment. |
| Reset the database volume | `docker compose down -v` then `docker compose up -d`. Day 1 has no seed data to lose. |

Stop the API with `Ctrl+C`. Leave Postgres running for the rest of the session, or `docker compose stop postgres` when you are done.
