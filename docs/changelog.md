# Day 12 changelog (since Day 11)

`git checkout day-12`. Previous branch: `day-11`.

## We learned

- Extract **Restaurant** (4th service) because of **org/ownership (Conway)**, not perf. Hardest extract: order pricing reads the menu on the **critical path**.
- **Event-carried state transfer:** `menu-updated` full snapshot → monolith `ordering.{restaurant_replica,menu_replica}`. Orders still price when Restaurant is down (`LocalReplica`).
- `SyncHttp` vs `LocalReplica` lever shows the temporal-coupling wound vs the fix.
- Zero-downtime backfill (`scripts/backfill-menu-replica.ps1`).

## Architecture

- Fourth service: `Tadka.Restaurant.Api` + `restaurant-db` :5436. Day-6 Redis cache **moved** here.
- Monolith **drops** the `restaurant` schema; no `Restaurant`/`MenuItem` domain types (BoundaryTests).
- Gateway: `/api/v1/restaurants/**` → Restaurant. Canonical shape: **4 services + gateway**.

## Code vs Day 11

| Area | What changed |
|---|---|
| `src/Tadka.Restaurant.Api/` | Menu, cache, Outbox → `menu-updated` |
| `IRestaurantPricingSource` | Local replica vs SyncHttp |
| `MenuUpdatedConsumer` | Idempotent upsert |
| Restaurant `CacheService` | Lua lock release (ADR-019) — cache lives here now |

ADRs **036, 037, 038, 039**.
