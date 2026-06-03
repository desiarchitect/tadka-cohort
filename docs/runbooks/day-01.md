# Day 1 — Runbook: Scaffold + `/health`

**Branch:** `day-01`  ·  **What exists today:** a Phase-0 .NET 10 scaffold and a single `/health` endpoint wired to PostgreSQL. Nothing else — and that's the point (you'll earn every other box over 8 weeks).

> New here? Read [`README.md`](README.md) first (prereqs, ports, the Windows-curl note).

## 1. Get the code running

```bash
git checkout day-01
docker compose up -d                 # starts Postgres (creds default to tadka/tadka_local)
docker compose ps                    # tadka-postgres should be (healthy)
dotnet run --project src/Tadka.Api
```

Expected on startup: the app applies migrations, then listens on `http://localhost:5224` (and `https://localhost:7036`).

## 2. Verify the health endpoint

```bash
curl http://localhost:5224/health
```
Expected (the first call is slow ~500–800 ms; call again for the real number):
```json
{ "status": "Healthy", "database": "Connected", "responseTime": "3ms", "timestamp": "..." }
```

Or open the **Scalar UI**: `https://localhost:7036/scalar/v2` and call `GET /health` from the browser.

## 3. The demo: prove health reflects the database

`/health` isn't a hard-coded "OK" — it actually checks the DB. Show it flip:

```bash
docker compose stop postgres
curl http://localhost:5224/health
# → 503, { "status": "Unhealthy", "database": "Disconnected", ... }

docker compose start postgres
curl http://localhost:5224/health
# → 200 Healthy again (give Postgres a few seconds to report healthy)
```

> Architect's aside: a health check that doesn't verify its dependencies lies to your load balancer. This one tells the truth.

## 4. (Optional) GitHub Copilot Agent-Mode demo

If you're doing the Copilot exercise: open the repo in VS Code, and prompt Copilot Agent Mode to add a `/health/ready` endpoint. Watch it read `.github/copilot-instructions.md` for project context, then test it. (Full script: the instructor pack's `cohort-prep/day-01/copilot-demo-script.md`.)

## ✅ Done when

- [ ] `dotnet build` is clean; `dotnet run` starts with no errors.
- [ ] `docker compose ps` shows `tadka-postgres` **healthy**.
- [ ] `GET /health` → `200` `"Healthy"` with `"database": "Connected"`.
- [ ] Stopping Postgres flips it to `503` `"Disconnected"`; starting it restores `200`.

## Troubleshooting

- **`/health` shows Disconnected on first run:** Postgres may still be starting — wait ~10 s, retry. Check `docker compose ps`.
- **Port 5224 in use:** a previous `dotnet run` is still up — stop it.
- **Reset everything:** `docker compose down -v && docker compose up -d`.

➡️ Next: [day-02.md](day-02.md) — the domain model and schema-per-domain.
