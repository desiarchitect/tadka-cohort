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

## Identity and ownership hardening carried onto this branch

| Area | What changed |
|---|---|
| Token signing | HS256 shared secret replaced by **RS256 + JWKS** (ADR-067). Payment, Delivery and Restaurant hold no signing secret; they fetch the monolith's public keys. `rotate-signing-key` (Admin) keeps current + 1 previous. |
| Scale-out | `Jwt:SigningKeyPem` makes every monolith replica sign with and publish the **same key**; Terraform, Azure and the `scale-out` compose profile supply it. |
| Sessions | **Refresh tokens** with rotation, atomic claim and family revocation (ADR-066); login **rate limit** (Redis-shared) and **account lockout** (ADR-065). |
| Ownership | Order-status ownership for restaurant owners; Payment `charge` Admin-only and payment reads owner-or-Admin; Delivery `/track`, `/location`, `/status` customer/rider/Admin scoped (ADR-031, 033). |
| Delivery | Riders are users (`DeliveryAgent.UserId`); orders with no free rider are **parked** and retried; `Delivered`/`Cancelled` release the rider; the rider claim is atomic. |
| PII | Card token is now a **keyed HMAC** (ADR-053); `Keycloak` optional issuer on port 8081 (`auth-prod` profile). |
| Consumers | A failed DLQ publish no longer stops the host: the consumer rewinds and retries (ADR-051). |

ADRs **052, 053, 065, 066, 067**.
