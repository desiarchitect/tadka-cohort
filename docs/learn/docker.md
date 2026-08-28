# Docker — enough to run Tadka

You do not need to be a Docker expert for Day 1. We use it so every laptop runs the **same PostgreSQL 16**, instead of installing Postgres on Windows, Mac, and Linux three different ways.

Install Docker Desktop first ([`SETUP.md`](../../SETUP.md)). This page is **how to use it** with this repo.

> Today `docker-compose.yml` starts **Postgres only**. Comments at the top of that file are a map of later weeks, not containers that exist now. Dockerfiles and multi-stage builds come much later, when we deploy.

---

## Three words

| Word | Meaning here |
|------|----------------|
| **Image** | The recipe. `postgres:16` is downloaded from Docker Hub once. |
| **Container** | A running instance of that image. Ours is named `tadka-postgres`. |
| **Compose** | Reads `docker-compose.yml` and starts the set of containers this day needs. |

```
postgres:16  (image)
     │  docker compose up -d
     ▼
tadka-postgres  (container)  →  localhost:5432
```

---

## Read today's compose file

Open [`docker-compose.yml`](../../docker-compose.yml) at the repo root.

| Key | Tadka's value | Why it matters |
|-----|----------------|----------------|
| **Service name** | `postgres` | What you pass to Compose: `docker compose stop postgres` |
| **Container name** | `tadka-postgres` | What you pass to `docker exec` |
| **Image** | `postgres:16` | Official Postgres 16 |
| **Ports** | `5432:5432` | Host port → container port. The API connects to `localhost:5432` |
| **User / password / db** | `tadka` / `tadka_local` / `tadka` | Dummy local creds, committed on purpose. See the runbook. |
| **Volume** | `pgdata` | Database files survive `stop` / `down`. `down -v` wipes them. |
| **Healthcheck** | `pg_isready -U tadka` | `docker compose ps` shows `(healthy)` only when Postgres can take connections |

**The Day 1 trap:** `docker compose stop db` fails. There is no service named `db`. The service is **`postgres`**.

Use **`docker compose`** (v2, a space). The old `docker-compose` binary is not what this repo documents.

**Windows:** Docker Desktop → Settings → General → *Use WSL 2 based engine*.

---

## Commands you will keep using

Run these from the **repo root** (where `docker-compose.yml` lives).

### Start infra (background)

```bash
docker compose up -d
```

Downloads `postgres:16` the first time, then starts `tadka-postgres` **detached** (`-d` = do not occupy this terminal).

### Is it up?

```bash
docker compose ps
```

**Look for:** `tadka-postgres` … `Up` … **`(healthy)`**. If it says `starting` or `unhealthy`, wait ~10 seconds and run it again.

### Why isn't it healthy?

```bash
docker compose logs postgres
```

Compose takes the **service** name. Last lines of the Postgres log usually say if the password, port, or data directory is the problem.

### Stop / start one service (leave the container on disk)

```bash
docker compose stop postgres     # Day 1: /health/ready should go 503
docker compose start postgres    # bring it back; wait until healthy
```

`stop` does **not** delete data. This is the liveness-vs-readiness demo.

### Stop and remove containers, keep the database

```bash
docker compose down
```

Containers go away. The `pgdata` volume stays. Next `up -d` is fast and your data is still there.

### Wipe the database (fresh start)

```bash
docker compose down -v
docker compose up -d
```

`-v` deletes volumes. Day 1 has no seed data to lose. From Day 2 on, the next `dotnet run` recreates schema.

### Run a command *inside* the container

```bash
docker exec tadka-postgres pg_isready -U tadka
# → localhost:5432 - accepting connections

docker exec -it tadka-postgres psql -U tadka -d tadka
```

`exec` takes the **container** name (`tadka-postgres`), not the service name. Day 2 uses `psql` to look at schemas.

---

## Quick map

| I want to… | Command |
|------------|---------|
| Start Postgres | `docker compose up -d` |
| See status | `docker compose ps` |
| Read logs | `docker compose logs postgres` |
| Pause Postgres (demo) | `docker compose stop postgres` |
| Resume Postgres | `docker compose start postgres` |
| Stop everything, keep data | `docker compose down` |
| Reset the database | `docker compose down -v` then `up -d` |
| Open `psql` | `docker exec -it tadka-postgres psql -U tadka -d tadka` |

---

## Troubleshooting

| Symptom | What to do |
|---------|------------|
| `docker` is not recognized | Docker Desktop is not installed, or the terminal was opened before Desktop finished starting. Open a new terminal. |
| `Compose` / `compose` not found | You have Compose v1. Install current Docker Desktop and use `docker compose`. |
| Port 5432 already in use | Another Postgres is bound to 5432. Stop it, or `docker compose down` a leftover stack. |
| Status never becomes healthy | `docker compose logs postgres`. Wrong password vs volume from an older compose file → `down -v` then `up -d`. |
| `stop db` / `stop tadka-postgres` as a compose command fails | Service name is `postgres`. Container name is `tadka-postgres` (for `docker exec` only). |
| API cannot connect | Compose healthy? `appsettings.Development.json` user/password `tadka` / `tadka_local`? |

Demo sequence with expected output: [`docs/runbooks/day-01.md`](../runbooks/day-01.md).
