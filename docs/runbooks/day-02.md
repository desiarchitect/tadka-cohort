# Day 2 — Runbook: Domain model & schema-per-domain

**Branch:** `day-02` · **What's new:** five bounded contexts in `Domain/`, one Postgres **schema** each, `InitialDomainModel` on startup, **ADR-003** and **ADR-008**. Day 1's liveness `/health` plus Copilot's `/health/ready` are already in the controller.

> **Windows PowerShell:** use **`curl.exe`**. From the repo root.

| Thing | Value |
|-------|--------|
| API (http) | `http://localhost:5224` |
| Compose service | `postgres` (container `tadka-postgres`) |
| Postgres | `localhost:5432`, db `tadka`, user `tadka`, password `tadka_local` |

Compose cheat sheet: [`docs/learn/docker.md`](../learn/docker.md).

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

A volume left over from Day 1 (or a previous Day 2 boot) makes the migration throw **`relation already exists`**.

```bash
docker compose down -v
docker compose up -d
docker compose ps
```

**Look for:** `tadka-postgres` … **`(healthy)`**.

```bash
docker exec tadka-postgres pg_isready -U tadka
# → localhost:5432 - accepting connections
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

```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "\dn"
```

**Look for:** `delivery`, `identity`, `ordering`, `payment`, `restaurant` (plus `public`).

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

```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "\d ordering.orders"
```

**Look for**

- `CustomerId` / `RestaurantId` columns with **no** `FOREIGN KEY` to `identity` or `restaurant`.
- `TotalAmount_Amount`, `TotalAmount_Currency`, `DeliveryAddress_*` — Money and Address live as **columns** on `orders`, not as separate tables (`OwnsOne`).

Within a schema, FKs **are** allowed:

```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "\d ordering.order_items"
```

**Look for** a FK from `order_items` → `ordering.orders`.

---

## 6. (Optional) Postgres down still does not kill liveness

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
| `relation already exists` on migrate | `docker compose down -v && docker compose up -d`, then `dotnet run` again. |
| `/health/ready` 404 | You are on Day 1 code, or the API was not rebuilt. This branch must have `Ready()`. |
| `/health` already returns `database` | Old controller. Day 2 splits liveness and readiness. |
| `psql` role `postgres` does not exist | User is **`tadka`**, database **`tadka`**. |
| Port 5224 in use | Stop the other `dotnet run`. |
| `curl` HTML / method error | Use `curl.exe` on PowerShell. |

➡️ Next: Day 3 — the REST API under `/api/v1`.
