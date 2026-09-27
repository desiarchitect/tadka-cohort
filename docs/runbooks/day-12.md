# Runbook — Day 12: Extract Restaurant (4th service) + zero-downtime migration

**What changed since Day 11:** [`docs/changelog.md`](../changelog.md).

Tadka reaches the canonical **4 services + a gateway**. Restaurant is the *last* and *hardest* extraction because order pricing reads its menu on the critical path — so it earns a **local read model** (event-carried state transfer, ADR-037) and a **zero-downtime data backfill** (ADR-038). Deploy is a black-box (`deploy/README.md`, ADR-039/064). This branch is intentionally kept fast-forwarded to `main` (`docs/runbooks/DAY-EVOLUTION.md`), so it also carries the live-tracking SSE hardening (ownership check + per-user stream cap) that landed after this branch's own Day-12 work — both get their own demo below since neither exists on Day 11.

## Start the stack
```bash
git checkout day-12
docker compose up -d                          # +restaurant-db (5436)
dotnet run --project src/Tadka.Restaurant.Api # :5260  (own DB, seeds, publishes menu-updated)
dotnet run --project src/Tadka.Api            # :5224  (applies the Day-12 migration on startup)
dotnet run --project src/Tadka.Gateway        # :8080
```
On monolith startup the Day-12 migration **drops** `restaurant.{restaurants,menu_items}` and **creates + seeds** `ordering.{restaurant_replica,menu_replica}`. If you're testing from a volume that already ran an earlier verification pass, run `docker compose down -v` first — a prior Demo 2 PATCH persists in the restaurant-db volume and will give you a ₹349 baseline instead of the documented ₹299.

Lever: `Ordering:RestaurantReadMode` = `LocalReplica` (default) | `SyncHttp`. Seeded password `Password123!`.

## Demo 1 — Restaurant down → orders still flow

**What you're proving:** the same wound Day 8 healed for Payment (a synchronous cross-service call on the order's hot path) exists again here in a worse spot — pricing, not just charging — and the same fix (a local read model) heals it.

```bash
# baseline (Restaurant up): login priya@tadka.test, place 2x Chicken Biryani at Meghana -> ₹598 (from the replica)
```
**Captured live: `total: 598.00`.**

```bash
# the wound:  ASPNETCORE: Ordering__RestaurantReadMode=SyncHttp ; restart monolith ; Ctrl+C Restaurant.Api ; POST /orders
```
**Captured live: `500 Internal Server Error`.** With the lever forcing a synchronous HTTP call to price the order, a dead Restaurant service takes the order path down with it — the exact Day-8 shape, now on pricing.

```bash
# the fix:    Remove-Item Env:\Ordering__RestaurantReadMode ; restart monolith (LocalReplica default) ; Restaurant STILL stopped ; POST /orders
```
**Captured live: `201 Created`, `₹598`.** Pricing came from `ordering.menu_replica`, not a live call. Restaurant being down only blocks *menu edits* — ordering, payment, and browsing are untouched.

## Demo 2 — Price change propagates (event-carried state transfer)

**What you're proving:** the replica isn't a one-time snapshot — it's kept live by Restaurant's own `menu-updated` events, the same Outbox→Kafka pattern Day 9 built for Ordering.

```bash
# login admin@tadka.test ; PATCH price 299->349 on the Restaurant service:
curl.exe -X PATCH http://localhost:5260/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu/b1b2c3d4-0001-4000-8000-000000000001 \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"price":{"amount":349,"currency":"INR"}}'        # -> 204
```
> The runbook previously showed this curl with literal `{meghana}`/`{biryani}` placeholders — copy-paste-unrunnable. Meghana is `a1b2c3d4-0001-4000-8000-000000000001`; Chicken Biryani is `b1b2c3d4-0001-4000-8000-000000000001` — both now inline above, matching the verification query below.

```bash
# menu-updated -> Outbox -> Kafka -> monolith MenuUpdatedConsumer upserts the replica:
docker exec tadka-postgres psql -U tadka -d tadka -c 'SELECT "Name","PriceAmount" FROM ordering.menu_replica WHERE "MenuItemId"='"'"'b1b2c3d4-0001-4000-8000-000000000001'"'"';'
# -> Chicken Biryani | 349.00 ;  a new 2x order now totals ₹698
```
**Captured live:** the PATCH returned `204`; ~4 seconds later `ordering.menu_replica` showed `349.00`; a fresh 2x order totalled **`₹698`**. The event carried the full new price — the monolith never called back to Restaurant to fetch it (ADR-008: no cross-schema reads, now no cross-service reads either).

## Demo 3 — Online data backfill (zero-downtime)
```powershell
./scripts/backfill-menu-replica.ps1 -SeedExtra 5000 -ChunkSize 1000 -ThrottleMs 50
# chunked (keyset) + FOR UPDATE SKIP LOCKED + throttle + replication-lag watch + idempotent upsert
# the table is never locked; reads never block. At scale -> CDC (Debezium).
```
**Captured live:** `5016 row(s)` backfilled in **`6 batches over 7s`**, replica lag staying at **`4-10ms`** throughout, the table never locked. Class mein strategy, script nahi — the LLD lives in the repo, not on the slide.

## Demo 4 — Restaurant service accept/reject (ADR-062, production multi-service path)

**What you're proving:** Day 11's refund saga (ADR-045) put the accept/reject *decision* inside Ordering, because Restaurant didn't exist as its own process yet. Now it does — the same refund machinery runs, but the decision genuinely lives in a different service and reaches Ordering over Kafka, not a local check.

See also **[decision-mode-matrix.md](decision-mode-matrix.md)**. Day 11 used **Inline** (Ordering decides). Day 12 earns **Service** (Restaurant.Api owns accept/reject). Same refund machinery either way — only the trigger moves.

```powershell
# Ordering always confirms; Restaurant.Api decides on order-confirmed.
# Terminal A (Restaurant.Api):  $env:Restaurant__AcceptMode="Reject"
# Terminal B (Ordering):        $env:Restaurant__DecisionMode="Service"
# Place an order → Confirmed → restaurant-response Rejected → cancel + refund-requested
```
**Captured live:** order showed `Created` immediately after payment, **`Cancelled`** ~30 seconds later, and `GET /api/v1/payments/{orderId}` on the Payment service showed **`Refunded`** with a fresh gateway reference. Give it real time here — this is a 3-hop Kafka chain (`order-confirmed` → `restaurant-response` → `refund-requested` → `payment-refunded`), longer than Demo 1's single hop or even Day 11's 2-hop refund saga.
```
# Stuck Confirmed? Restaurant.Api/Kafka down — see decision-mode-matrix.md runbook.
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
**Captured live:** the dual-write PATCH updated both `Name` and `DisplayName` to the same value in one call — verified directly against `restaurant.menu_items`. The chunked backfill converged correctly (NULL `DisplayName` count reached 0, no errors, no lock waits) across the ~5,000 synthetic rows Demo 3 seeded.

> **A real bug found and fixed running this script:** `expand-contract-demo.ps1` failed outright under Windows PowerShell 5.1 with a cascade of parser errors (`An expression was expected after '('`, `Missing argument in parameter list`) that looked nothing like the actual mistake. The file's em dashes had been corrupted into a 3-character mojibake sequence at some point (`â€"` instead of `—`) with no byte-order mark, and separately every embedded SQL identifier used a PowerShell-invalid `\"` escape (PowerShell isn't Bash — a backslash doesn't escape a quote). Fixed both: added a UTF-8 BOM and replaced the corrupted bytes with plain ASCII, and switched the embedded double quotes to PowerShell's own `` `" `` escape (single-line strings) or removed them entirely (inside `@"..."@` here-strings, which don't need quote-escaping at all). **A separate, unresolved observation:** on this machine, the chunked loop advanced one row per iteration instead of up to `-ChunkSize`, even though a direct `docker exec ... psql -c` run of the identical query correctly updated multiple rows in one call. The backfill still converged correctly and never locked — just slower than the chunk size implies. Root cause not confirmed (a Windows Docker Desktop npipe stdin/stdout buffering quirk under `docker exec -i ... -f -` is the leading suspect); worth watching if you run this demo live.

## Demo 6 — The live-tracking SSE stream: ownership + a per-user stream cap

**What you're proving:** two hardening fixes landed on this branch after its own Day-12 work, because they apply everywhere the SSE endpoint exists, not just on the day they were found. Day 10 taught resource ownership as a REST rule; this is the same rule, now enforced on the SSE endpoint too. And because the SSE path bypasses the cloud gateway's Front Door origin lock (ADR-064), a single user could otherwise hold unlimited concurrent streams — so there's a cap.

```bash
# Priya (the order's owner) streams her own order — fine:
curl -s -N http://localhost:5224/api/v1/orders/$ORDER/events -H "Authorization: Bearer $TOKEN"   # event: ... (Ctrl+C to stop)
# Rahul (a different customer) tries the same order id:
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5224/api/v1/orders/$ORDER/events -H "Authorization: Bearer $RAHUL"   # 403
```
**Captured live: `403`** for Rahul, before the stream ever opens.

```bash
# Priya opens 3 concurrent streams on the same order (the default cap), then a 4th:
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:5224/api/v1/orders/$ORDER/events -H "Authorization: Bearer $TOKEN"   # 429
```
**Captured live: `429`** on the 4th concurrent stream; closing one of the first three frees the slot immediately (the lease is released in a `finally`, so a dropped client never burns a slot forever). Default cap is 3 per user (`SseStreamLimiter`, constructor default `maxPerUser`).

## Demo 7 — Replica staleness has a signal now (ADR-063), briefly

**What you're proving:** ADR-037's local read model trades freshness for availability — and until this fix, nothing measured that trade. `tadka.replica.lag_seconds` closes that gap, riding the same OTEL pipeline Day 13 builds properly.

This isn't a full demo — Day 13 hasn't happened yet in the taught sequence, and a real dashboard needs that day's Collector/Prometheus/Grafana stack running. What you *can* show without it: the calculation and the wiring are both unit-tested without Docker or a broker.
```bash
dotnet test tests/Tadka.Api.Tests --filter "FullyQualifiedName~ReplicaLag"   # 5/5, no Docker needed
```
If the Day-13 stack happens to be up (`OTEL_EXPORTER_OTLP_ENDPOINT` set), the gauge flows to Grafana like every other `tadka.*` metric — nothing extra to configure. Full treatment: `docs/adrs/063-replica-lag-metric.md`.

## Demo 8, deferred — the live Azure cloud walk (ADR-064)

The teaching script's Segment 7 walks a *real*, currently-deployed Azure Container Apps stack (Front Door CDN cache, private Postgres, hop-counting through the platform's own Envoy sidecars, the SSE-bypasses-CDN pattern). That's real cloud infrastructure costing real money, requiring the instructor's own Azure credentials and a pre-class `cloud-up.ps1 -Mode basic` run — **not something to bring up as part of running this runbook**, and nothing in this section was executed live for this pass. If you have that stack up: `deploy/README.md` and `docs/adrs/064-live-cloud-deployment-azure-container-apps.md` are the reference; the teaching script's `[S7-B5]` has the exact walkthrough (CDN cache-hit check, `docker compose`-to-Azure-box mapping, hop count, gateway-vs-CDN for SSE). Remember `cloud-down.ps1` after.

## What changed
- NEW `src/Tadka.Restaurant.Api` — own Postgres (5436), per-service JWT, Redis cache, publishes `menu-updated` via Outbox→Kafka; consumes `order-confirmed` for accept/reject (ADR-062).
- Monolith — dropped the `restaurant` schema; added `ordering.{restaurant_replica,menu_replica}` + `MenuUpdatedConsumer`; prices orders from `IRestaurantPricingSource` (LocalReplica/SyncHttp); `RestaurantResponseConsumer` for Service decision mode.
- Gateway — routes `/api/v1/restaurants/**` to the Restaurant service.
- `scripts/backfill-menu-replica.ps1`, `expand-contract-demo.ps1`, `evolution-break.ps1`; `deploy/README.md` (cloud black-box reference, superseded in part by ADR-064's live Azure deployment).
- Carried forward from `main` (not part of Day 12's own work, but present on this branch): the SSE ownership check + per-user stream cap (Demo 6), the replica-lag metric (ADR-063, Demo 7), and the live Azure deployment (ADR-064, Demo 8).

## Ports
monolith :5224 · payment :5240 · delivery :5250 · restaurant :5260 · gateway :8080 · postgres 5432 / replica 5433 / payment-db 5434 / delivery-db 5435 / restaurant-db 5436 · redis 6379 · kafka 9092 / kafka-ui 8090 · **PgBouncer 6432** (Day 11, carried forward)

PgBouncer is not a Day-12 teaching beat — the demo lives on `day-11`. The service stays in compose so later days don't lose it.

## Wiring Reference & Cross-Stack Architecture

### Where the code lives in Tadka
- **Restaurant extraction (ADR-036):** [`src/Tadka.Restaurant.Api/Controllers/RestaurantsController.cs`](../../src/Tadka.Restaurant.Api/Controllers/RestaurantsController.cs), [`Data/RestaurantDbContext.cs`](../../src/Tadka.Restaurant.Api/Data/RestaurantDbContext.cs) (own Postgres, own migration history).
- **Local read model (ADR-037):** [`src/Tadka.Api/Infrastructure/RestaurantReadModel/LocalReplicaPricingSource.cs`](../../src/Tadka.Api/Infrastructure/RestaurantReadModel/LocalReplicaPricingSource.cs) and `HttpRestaurantPricingSource.cs` (both implement `IRestaurantPricingSource`), wired in `Program.cs` off `Ordering:RestaurantReadMode`.
- **Zero-downtime backfill (ADR-038):** [`scripts/backfill-menu-replica.ps1`](../../scripts/backfill-menu-replica.ps1), [`scripts/expand-contract-demo.ps1`](../../scripts/expand-contract-demo.ps1).
- **Gateway route:** [`src/Tadka.Gateway/appsettings.json`](../../src/Tadka.Gateway/appsettings.json) — `/api/v1/restaurants/{**remainder}` match.
- **SSE ownership + cap:** [`src/Tadka.Api/Controllers/OrderTrackingController.cs`](../../src/Tadka.Api/Controllers/OrderTrackingController.cs), [`Infrastructure/Realtime/SseStreamLimiter.cs`](../../src/Tadka.Api/Infrastructure/Realtime/SseStreamLimiter.cs).
- **Replica-lag metric (ADR-063):** [`src/Tadka.Telemetry/TadkaDiagnostics.cs`](../../src/Tadka.Telemetry/TadkaDiagnostics.cs), [`src/Tadka.Api/Infrastructure/Messaging/MenuUpdatedConsumer.cs`](../../src/Tadka.Api/Infrastructure/Messaging/MenuUpdatedConsumer.cs).

### Cross-Stack Implementation Matrix

| Concern | .NET Core (This Repo) | Java (Spring Boot) | Node.js (TypeScript) | Go |
|---|---|---|---|---|
| **Local read model, fed by events** | EF Core upsert in `MenuUpdatedConsumer` | Spring Kafka listener + JPA upsert | kafkajs consumer + Prisma upsert | kafka-go consumer + sqlc |
| **Zero-downtime migration** | Hand-rolled chunked scripts (this repo) | Flyway (expand-contract migrations) | Prisma Migrate | goose |
| **Continuous CDC at scale** | Debezium (any stack — Kafka Connect) | Debezium | Debezium | Debezium |
| **Reverse-proxy gateway, 4th route** | YARP | Spring Cloud Gateway | Express-gateway | Kong / Envoy / Traefik |
| **Cloud deploy shape** | Terraform → ECS/Container Apps (this repo, ADR-039/064) | same cloud primitives, any stack | same cloud primitives, any stack | same cloud primitives, any stack |

**Pattern is language-neutral.** Event-carried state transfer and expand-contract migrations are architecture ideas, not .NET ideas — the same shape shows up wherever a service needs its own copy of another service's data on a hot path. Full matrix: `day-12/option-space.md`.

## Demo vs. Production: named gaps, not overclaimed features
- **No auto-timeout reject in Service decision mode.** If Restaurant.Api never responds (down, or its consumer stuck), the order sits `Confirmed` forever — there's no timeout-then-cancel. Named as a revisit in ADR-062, not silently assumed away.
- **The replica-lag metric has no alert wired.** Unlike Day 14's `tadka.payment.circuit_transitions` (which got a Grafana panel), a stalled replica is visible if someone looks at the gauge, not paged on. Named directly in ADR-063.
- **The SSE stream cap is a single in-memory counter per process.** It caps concurrent streams *per instance*, not truly per user across a horizontally-scaled deployment — fine for this teaching build, not the real answer at scale (a shared counter, e.g. Redis, would be).
- **PgBouncer is present in this branch's compose but isn't wired into the cloud deployment (ADR-064) yet.** The local demo (Day 11) and the live cloud stack are two separate stories that haven't been reconciled.

## Reset
`docker compose down -v` clears all volumes (incl. the backfill's synthetic rows).

## Tests
Live count as of this pass (Docker up, `dotnet test`): **117/117** — monolith 69, Payment 20, Delivery 5, Restaurant 8, and a wholly new `Tadka.Gateway.Tests` project (15, `FrontDoorOriginLockTests` among them) that no prior count in this file ever included. The branch's own earlier note ("39/39 at landing, 90/90 as of a later check") is honest about *why* the number moves — `day-12` stays fast-forwarded to `main` — but 90/90 is itself now stale: three more commits (Azure deploy code + its new Gateway.Tests project, the SSE ownership fix, the SSE stream cap) landed after that number was written. **Re-run `dotnet test` yourself before class — this number will move again.**

## Troubleshooting
- **Menu PATCH curl 404s or looks wrong:** use the real GUIDs (`a1b2c3d4-0001-...` for Meghana, `b1b2c3d4-0001-...` for Chicken Biryani), not literal placeholder text — see Demo 2's note.
- **Baseline order isn't ₹598:** a prior verification pass already ran Demo 2's PATCH and the restaurant-db volume kept the ₹349 price. `docker compose down -v` and re-seed.
- **`expand-contract-demo.ps1` throws parser errors on Windows PowerShell:** you have the pre-fix version of the script. It's fixed in this pass — pull the branch again if you still see this.
- **Stuck `Confirmed` in Demo 4 (Service mode):** Restaurant.Api or Kafka is down, or its consumer isn't running — see `decision-mode-matrix.md`'s stuck-Confirmed runbook. Fallback: flip `Restaurant__DecisionMode` back to `Inline` for the next order.
- **SSE stream returns 403 for the order's real owner:** wrong token — re-login and confirm the `sub` claim matches the order's `customerId`.
- **SSE stream returns 429 for a first attempt:** a previous test session's streams are still open somewhere and haven't released their slots — check for stray `curl -N` processes.
- **PgBouncer `SHOW POOLS` fails with "not allowed":** connect as the `tadka` user (set via `ADMIN_USERS` in `docker-compose.yml`), not `postgres`.
