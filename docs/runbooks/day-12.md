# Runbook — Day 12: Extract Restaurant (4th service) + zero-downtime migration

Tadka reaches the canonical **4 services + a gateway**. Restaurant is the *last* and *hardest* extraction because order pricing reads its menu on the critical path — so it earns a **local read model** (event-carried state transfer, ADR-037) and a **zero-downtime data backfill** (ADR-038). Deploy is a black-box (`deploy/README.md`, ADR-039).

## Start the stack
```bash
git checkout day-12
docker compose up -d                          # +restaurant-db (5436)
dotnet run --project src/Tadka.Restaurant.Api # :5260  (own DB, seeds, publishes menu-updated)
dotnet run --project src/Tadka.Api            # :5224  (applies the Day-12 migration on startup)
dotnet run --project src/Tadka.Gateway        # :8080
```
On monolith startup the Day-12 migration **drops** `restaurant.{restaurants,menu_items}` and **creates + seeds** `ordering.{restaurant_replica,menu_replica}`.

Lever: `Ordering:RestaurantReadMode` = `LocalReplica` (default) | `SyncHttp`. Seeded password `Password123!`.

## Demo 1 — Restaurant down → orders still flow
```bash
# baseline (Restaurant up): login priya@tadka.test, place 2x Chicken Biryani at Meghana -> ₹598 (from the replica)
# the wound:  ASPNETCORE: Ordering__RestaurantReadMode=SyncHttp ; stop Restaurant ; POST /orders -> FAILS
# the fix:    default LocalReplica ; Restaurant still stopped ; POST /orders -> 201 Created, ₹598
```

## Demo 2 — Price change propagates (event-carried state transfer)
```bash
# login admin@tadka.test ; PATCH price 299->349 on the Restaurant service:
curl.exe -X PATCH http://localhost:5260/api/v1/restaurants/{meghana}/menu/{biryani} \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"price":{"amount":349,"currency":"INR"}}'        # -> 204
# menu-updated -> Outbox -> Kafka -> monolith MenuUpdatedConsumer upserts the replica:
docker exec tadka-postgres psql -U tadka -d tadka -c 'SELECT "Name","PriceAmount" FROM ordering.menu_replica WHERE "MenuItemId"='"'"'b1b2c3d4-0001-4000-8000-000000000001'"'"';'
# -> Chicken Biryani | 349.00 ;  a new 2x order now totals ₹698
```

## Demo 3 — Online data backfill (zero-downtime)
```powershell
./scripts/backfill-menu-replica.ps1 -SeedExtra 5000 -ChunkSize 1000 -ThrottleMs 50
# chunked (keyset) + FOR UPDATE SKIP LOCKED + throttle + replication-lag watch + idempotent upsert
# the table is never locked; reads never block. At scale -> CDC (Debezium).
```

## Demo 4 — Restaurant service accept/reject (ADR-062, production multi-service path)
```powershell
# Ordering always confirms; Restaurant.Api decides on order-confirmed.
# Terminal A (Restaurant.Api):  Restaurant__AcceptMode=Reject
# Terminal B (Ordering):        Restaurant__DecisionMode=Service
# Place an order → Confirmed briefly → restaurant-response Rejected → cancel + refund-requested
# With Saga__Mode=Orchestration, query:
#   SELECT * FROM ordering.saga_instances ORDER BY "StartedAt" DESC LIMIT 5;
```

## Demo 5 — Expand-contract dual-write (ADR-038 live)
```powershell
# Demo__DualWriteDisplayName=true on Restaurant.Api, then PATCH a menu name.
# Both Name and DisplayName columns update; MapItem prefers DisplayName when set.
./scripts/expand-contract-demo.ps1                 # chunked backfill Name -> DisplayName
./scripts/expand-contract-demo.ps1 -BreakGiantUpdate  # contrast: one giant UPDATE
```

## What changed
- NEW `src/Tadka.Restaurant.Api` — own Postgres (5436), per-service JWT, Redis cache, publishes `menu-updated` via Outbox→Kafka; consumes `order-confirmed` for accept/reject (ADR-062).
- Monolith — dropped the `restaurant` schema; added `ordering.{restaurant_replica,menu_replica}` + `MenuUpdatedConsumer`; prices orders from `IRestaurantPricingSource` (LocalReplica/SyncHttp); `RestaurantResponseConsumer` for Service decision mode.
- Gateway — routes `/api/v1/restaurants/**` to the Restaurant service.
- `scripts/backfill-menu-replica.ps1`, `expand-contract-demo.ps1`, `evolution-break.ps1`; `deploy/README.md` (cloud black-box reference).

## Ports
monolith :5224 · payment :5240 · delivery :5250 · restaurant :5260 · gateway :8080 · postgres 5432 / replica 5433 / payment-db 5434 / delivery-db 5435 / restaurant-db 5436 · redis 6379 · kafka 9092 / kafka-ui 8090

## Reset
`docker compose down -v` clears all volumes (incl. the backfill's synthetic rows).

## Tests
`dotnet test` → **39/39** (monolith 28 + payment 5 + delivery 3 + restaurant 3).
