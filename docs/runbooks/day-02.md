# Day 2 — Runbook: Domain model & schema-per-domain

**Branch:** `day-02` · **What's new:** five bounded contexts in `Domain/`, one Postgres **schema** each, `InitialDomainModel` on startup, **ADR-003** and **ADR-008**. Day 1's liveness `/health` plus Copilot's `/health/ready` are already in the controller.

> **Windows PowerShell:** use **`curl.exe`**. From the repo root.

| Thing | Value |
|-------|--------|
| API (http) | `http://localhost:5224` |
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

Container must be **healthy** (`docker compose ps`). Run the API once so `InitialDomainModel` creates the schemas.

**Do not** use Host `tadka-postgres` — that name only works inside Docker.

If connect fails: a **local** Postgres install may own `5432`. Stop that service, or you are hitting the wrong server. `docker compose ps` PORTS must show `0.0.0.0:5432->5432`.

In pgAdmin: **tadka → Schemas**. Look for `ordering`, `restaurant`, `delivery`, `identity`, `payment` (plus `public`). Tables live **under each schema**, not only under `public`. Folder `Users` in code is schema `identity` here.

### Docker — enough to run this runbook

You do not install Postgres on Windows. **Docker Desktop** runs official Postgres 16. Start Docker Desktop, then run every command from the **repo root** (where `docker-compose.yml` lives). Use `docker compose` (space), not `docker-compose`.

| Word | Meaning here |
|------|----------------|
| **Image** | Recipe. `postgres:16` is downloaded once. |
| **Container** | Running copy. Named `tadka-postgres`. |
| **Compose** | Starts whatever `docker-compose.yml` lists. Today: Postgres only. |
| **Volume** | Disk for DB files. Survives `stop`. Wiped by `down -v`. |

| Name | What you type it with |
|------|------------------------|
| **Service** `postgres` | `up`, `stop`, `start`, `logs`, `down` |
| **Container** `tadka-postgres` | `docker exec` (and what `ps` shows under NAME) |

**Every Docker command in this file**

| Command | What it does |
|---------|----------------|
| `docker compose down -v` | Stop and **remove** the container, network, **and volume**. Empty database. Required when switching onto `day-02` so the migration does not hit “relation already exists”. |
| `docker compose up -d` | Create/start Postgres **in the background** (`-d` = this terminal stays free). |
| `docker compose ps` | List this project's containers. Want `tadka-postgres` **`(healthy)`**. |
| `docker exec tadka-postgres pg_isready -U tadka` | Inside the container: is Postgres accepting connections? Not a schema list. |
| `docker exec tadka-postgres psql …` | Inside the container: run **one** SQL/`psql` command as user `tadka` on database `tadka`, then exit. |
| `docker compose stop postgres` | Pause the DB. Data stays. API can keep running (liveness vs ready). |
| `docker compose start postgres` | Resume the same container. Wait for `(healthy)`. |

`psql` pieces: `-U tadka` = user, `-d tadka` = database, `-c "…"` = run this and quit.

`psql` backslash commands (they are **not** SQL):

| Inside `-c "…"` | Meaning |
|-----------------|---------|
| `\dn` | List **schemas** (namespaces). Day 2 payoff: five domains plus `public`. |
| `\dt ordering.*` | List **tables** in schema `ordering`. |
| `\d restaurant.menu_items` | **Describe** that table (columns, constraints). |
| `\d ordering.orders` | Same for `orders` — look for IDs with **no** cross-schema FK. |

**“container name already in use”:** another clone (e.g. `tadka` vs `tadka-cohort`) already started `tadka-postgres`. From **that** folder: `docker compose down -v`, then `up -d` here. Only one container can use that name and port `5432`.

---

## 0. What the tree should look like

```bash
git checkout day-02
dotnet build Tadka.slnx
```

**Look for**

- Build: **0 errors, 0 warnings**.
- Two projects only: `Tadka.Api` and `Tadka.Api.Tests`.
- `Domain/{Orders,Restaurants,Delivery,Users,Payments}` have **classes**, plus `ValueObjects/{Money,Address,GeoLocation}`.
- Folder `Users` vs schema `identity` — intentional (Day 2 teaches that).
- No `k6/`, `terraform/`, extra `src/` services.
- `GET /health` is liveness; `GET /health/ready` hits `TadkaDbContext`.

---

## 1. Fresh Postgres (the `-v` matters)

A volume left over from Day 1 (or a previous Day 2 boot) makes the migration throw **`relation already exists`**. `-v` wipes that volume on purpose.

```bash
docker compose down -v
docker compose up -d
docker compose ps
```

**Look for:** `tadka-postgres` … **`(healthy)`**.

```bash
docker exec tadka-postgres pg_isready -U tadka
# → localhost:5432 - accepting connections   (server is up; not a list of schemas)
```

---

## 2. Run the API (migration on startup)

Keep this terminal open.

```bash
dotnet run --project src/Tadka.Api
```

**Look for**

- `Applying migration '20260601154343_InitialDomainModel'.` (first boot on a fresh volume)
- `Now listening on: http://localhost:5224`

There are still **no** `/api/v1` endpoints. That is Day 3.

---

## 3. Liveness vs readiness

Second terminal, repo root:

```bash
curl.exe -s -w "\nHTTP %{http_code}\n" http://localhost:5224/health
```

**Look for — HTTP 200**, **no** `database` field:

```json
{ "status": "Healthy", "timestamp": "..." }
```

```bash
curl.exe -s -w "\nHTTP %{http_code}\n" http://localhost:5224/health/ready
```

**Look for — HTTP 200**

```json
{ "status": "Healthy", "database": "Connected", "responseTime": "3ms", "timestamp": "..." }
```

First ready hit after `dotnet run` is often **500–800 ms**. Hit it again for single-digit ms.

---

## 4. The payoff — five schemas

`exec` = run this inside `tadka-postgres`. `psql` = Postgres client. `\dn` = list schemas.

```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "\dn"
```

**Look for:** `delivery`, `identity`, `ordering`, `payment`, `restaurant` (plus `public`).

`\dt schema.*` lists tables in that schema:

```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "\dt ordering.*"
docker exec tadka-postgres psql -U tadka -d tadka -c "\dt restaurant.*"
docker exec tadka-postgres psql -U tadka -d tadka -c "\dt delivery.*"
docker exec tadka-postgres psql -U tadka -d tadka -c "\dt identity.*"
docker exec tadka-postgres psql -U tadka -d tadka -c "\dt payment.*"
```

**Look for (9 tables)**

| Schema | Tables |
|--------|--------|
| `ordering` | `orders`, `order_items` |
| `restaurant` | `restaurants`, `menu_items` |
| `delivery` | `delivery_agents`, `delivery_assignments` |
| `identity` | `users`, `user_addresses` |
| `payment` | `payments` |

---

## 5. ADR-008 and value objects — prove it in the table

No seed data today (that is Day 3). `\d` and the FK query need empty tables.

`\d table` describes columns and constraints:

```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "\d restaurant.menu_items"
```

**Look for** `Price_Amount` and `Price_Currency` as columns on `menu_items` — EF `OwnsOne` flattened the Money value object. There is no `money` table.

```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "\d ordering.orders"
```

**Look for**

- `CustomerId` / `RestaurantId` columns with **no** `FOREIGN KEY` to `identity` or `restaurant`.
- `TotalAmount_Amount`, `TotalAmount_Currency`, `DeliveryAddress_*` — Money and Address live as **columns** on `orders`, not as separate tables (`OwnsOne`).

Prove ADR-008 with a query that returns **0 rows** (no seed required):

```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT tc.table_schema, tc.table_name, ccu.table_schema AS foreign_schema, ccu.table_name AS foreign_table FROM information_schema.table_constraints tc JOIN information_schema.constraint_column_usage ccu ON ccu.constraint_name = tc.constraint_name AND ccu.constraint_schema = tc.constraint_schema WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema <> ccu.table_schema;"
```

Within a schema, FKs **are** allowed:

```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "\d ordering.order_items"
```

**Look for** a FK from `order_items` → `ordering.orders`.

---

## 6. (Optional) Postgres down still does not kill liveness

`stop postgres` = service name. Container and data stay. Keep `dotnet run` going.

```bash
docker compose stop postgres
curl.exe -s -w "\nHTTP %{http_code}\n" http://localhost:5224/health/ready
curl.exe -s -w "\nHTTP %{http_code}\n" http://localhost:5224/health
```

**Look for:** ready **503** / `"Disconnected"`; `/health` still **200**.

```bash
docker compose start postgres
docker compose ps
# wait until healthy, then:
curl.exe -s -w "\nHTTP %{http_code}\n" http://localhost:5224/health/ready
```

---

## 7. Walk the code (no command)

Open in the editor, font bumped:

- `Domain/Orders/Order.cs` — aggregate root; `CustomerId` / `RestaurantId` are Guids
- `Domain/ValueObjects/Money.cs` — no identity, immutable
- `Data/TadkaDbContext.cs` — `ToTable("orders", "ordering")` and friends
- `docs/adrs/003-schema-per-domain.md` and `008-no-cross-schema-fks.md`

---

## Done when

- [ ] `dotnet build Tadka.slnx` is clean.
- [ ] `down -v` then `up -d` → `tadka-postgres` healthy.
- [ ] `dotnet run` applies `InitialDomainModel` with no error.
- [ ] `GET /health` → 200, no `database` field.
- [ ] `GET /health/ready` → 200, `"Connected"`.
- [ ] `\dn` shows the five domain schemas.
- [ ] `ordering.orders` has no FK to `identity` or `restaurant`.
- [ ] You can point at a value object stored as columns, not a table.

---

## Troubleshooting

| Symptom | What to do |
|---------|------------|
| `relation already exists` on migrate | `docker compose down -v && docker compose up -d`, then `dotnet run` again. `-v` wipes the old volume. |
| `/health/ready` 404 | You are on Day 1 code, or the API was not rebuilt. This branch must have `Ready()`. |
| `/health` already returns `database` | Old controller. Day 2 splits liveness and readiness. |
| `psql` role `postgres` does not exist | User is **`tadka`**, database **`tadka`**. |
| Port 5224 in use | Stop the other `dotnet run`. |
| `curl` HTML / method error | Use `curl.exe` on PowerShell. |

### Container name already in use

This means another checkout has already created the fixed container name `tadka-postgres`. Check whether it is healthy before removing anything:

```powershell
docker ps -a --filter "name=^/tadka-postgres$"
docker inspect tadka-postgres --format '{{json .Config.Labels}}'
```

If the container is `Up` and `(healthy)`, reuse it with the Tadka Compose project name:

```powershell
docker compose -p tadka up -d
docker compose -p tadka ps
docker exec tadka-postgres pg_isready -U tadka -d tadka
```

This repository pins the project name to `tadka`, so `docker compose up -d` also works after pulling the latest Day 2 files.

If the existing container is stale and you do not need its database, remove it and start a fresh one:

```powershell
docker rm -f tadka-postgres
docker compose up -d
docker compose ps
```

**Do not** run `docker compose down -v` unless you intentionally want to delete the PostgreSQL volume and all database data. To clean up the old checkout without deleting data, use `docker compose down` (without `-v`) from that checkout, then run `docker compose up -d` here.

➡️ Next: Day 3 — the REST API under `/api/v1`.
