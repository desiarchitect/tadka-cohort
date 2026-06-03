# Day 2 — Runbook: Domain Model & Schema-per-Domain

**Branch:** `day-02`  ·  **What's new:** the DDD domain model (aggregates, entities, value objects) and the EF Core `DbContext`, mapped to **schema-per-domain** in Postgres (`ordering`, `restaurant`, `delivery`, `identity`, `payment`). The migration creates all tables + seed data. There are **no HTTP business endpoints yet** (those arrive Day 3) — today you verify the *data model*.

> New here? Read [`README.md`](README.md) first.

## 1. Run it

```bash
git checkout day-02
docker compose up -d
dotnet run --project src/Tadka.Api
```
On startup the app applies the **InitialDomainModel** migration (creates the 5 schemas, tables, and seeds 3 restaurants + 16 menu items + 1 customer), then serves `/health`.

```bash
curl http://localhost:5224/health      # 200 Healthy — app + DB wired
```

## 2. Verify the schemas & tables (this is the day's payoff)

Each domain lives in its **own schema** (future service boundary). Inspect with `psql` inside the container:

```bash
# the 5 domain schemas
docker exec tadka-postgres psql -U tadka -d tadka -c "\dn"
# → ordering, restaurant, delivery, identity, payment  (+ public)

# tables per schema
docker exec tadka-postgres psql -U tadka -d tadka -c "\dt ordering.*"     # orders, order_items
docker exec tadka-postgres psql -U tadka -d tadka -c "\dt restaurant.*"   # restaurants, menu_items
docker exec tadka-postgres psql -U tadka -d tadka -c "\dt delivery.*"     # agents, assignments
docker exec tadka-postgres psql -U tadka -d tadka -c "\dt identity.*"     # users, user_addresses
docker exec tadka-postgres psql -U tadka -d tadka -c "\dt payment.*"      # payments
```

## 3. Verify the seed data

```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT name, is_active FROM restaurant.restaurants;"
# → Meghana Foods | Truffles | Vidyarthi Bhavan

docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT count(*) FROM restaurant.menu_items;"   # 16

# Value objects are embedded columns (no separate table) — e.g. a menu item's Money (price + currency):
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT name, price, currency FROM restaurant.menu_items LIMIT 3;"

# No cross-schema FK (ADR-008): an order references a customer by id only, not a DB foreign key.
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT name, email FROM identity.users;"   # Priya Sharma
```

> Notice: `Money`, `Address`, `GeoLocation` are **value objects** mapped to columns on their parent table (EF `OwnsOne`) — no junk join tables. And there are **no foreign keys across schemas** — that's what makes each schema cleanly extractable into a service later.

## ✅ Done when

- [ ] `dotnet run` applies the migration with no error; `/health` is `Healthy`.
- [ ] `\dn` shows the 5 domain schemas.
- [ ] `restaurant.restaurants` has the 3 seeded restaurants; `restaurant.menu_items` has 16 rows.
- [ ] You can point to where a value object (e.g. price) lives as **columns**, not a separate table.

## Troubleshooting

- **Migration didn't seem to run:** it runs once per fresh DB. Reset with `docker compose down -v && up -d`, then `dotnet run`.
- **Want to re-seed from scratch:** `docker compose down -v` wipes the volume; next `dotnet run` re-creates + re-seeds.

➡️ Next: [day-03.md](day-03.md) — the full REST API you can actually call.
