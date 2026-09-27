# Day 11 — Runbook: Extract Delivery (3rd service) + the API Gateway

**Branch:** `day-11`  ·  **What changed since Day 10:** [`docs/changelog.md`](../changelog.md). **What's new:** the 3rd service — **`Tadka.Delivery.Api`** (own DB `delivery-db` 5435; **Redis-geo** live location; Kafka-driven assignment, ADR-033/034) — and a **YARP API gateway** (`Tadka.Gateway`, :8080, single entry + edge rate-limit, ADR-035). The order→payment→**delivery** flow is now a **3-participant Saga**, and it just earned a real 4th failure mode: a restaurant can **reject** an already-paid order, which needs a **compensating refund** (ADR-045) — Day 9's choreography pattern, reused. **3 services + gateway** (monolith :5224, payment :5240, delivery :5250, YARP :8080). Restaurant is still in the monolith — the 4th service is Day 12. Also: **PgBouncer** (`:6432`, ADR-015 landed) — the Day-5 pool-exhaustion promise, paid off now that 2+ app instances actually exist.

> New here? Read [`README.md`](README.md). Windows PowerShell → `curl.exe`. Demo password `Password123!`. Deep Saga treatment: `cohort-prep/day-11/saga-deep-dive.md` (instructor pack, not in this repo).

## 1. Run it (infra + 3 services + gateway)

Bring up infra first — Delivery needs its own Postgres (`delivery-db`, 5435) and PgBouncer (`6432`) now sits next to it, both new since Day 10:

```bash
git checkout day-11
docker compose up -d            # + delivery-db (5435), pgbouncer (6432); wait for tadka-kafka + delivery-db healthy
docker compose ps                # confirm all containers report healthy before starting any app
```

Four processes, four terminals — the gateway goes last, since it only has something to route to once the other three are listening:

```bash
dotnet run --project src/Tadka.Payment.Api     # :5240
dotnet run --project src/Tadka.Delivery.Api    # :5250 (consumes order-confirmed; seeds riders)
dotnet run --project src/Tadka.Api             # :5224
dotnet run --project src/Tadka.Gateway         # :8080 (the single entry point)
```

## 2. The 3-service Saga: order → payment → delivery (ADR-029/033)

**What you're proving:** the Saga that Day 9 built for 2 participants (order + payment) grows a 3rd leg without changing its shape — Delivery reacts to an event it never asked for, the same way Payment did on Day 9.

```bash
TOKEN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"priya@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
BODY='{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"a1b2c3d4-0001-4000-8000-000000000001","items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":1}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.93,"longitude":77.61}}'
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
sleep 3
curl -s http://localhost:5224/api/v1/orders/$ORDER -H "Authorization: Bearer $TOKEN" | sed -E 's/.*"status":"([^"]+)".*/order: \1/'   # Confirmed (payment saga)
curl -s http://localhost:5250/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $TOKEN"   # {"agentName":"<first available>","status":"Assigned",...}
# Assignment is FirstOrDefault(Available) — NOT nearest / GEOSEARCH. Whoever is Available wins. Reset agents if a prior run left them OnDelivery.
```
Flow: order Created → paid → **Confirmed** → monolith publishes `order-confirmed` (Outbox→Kafka) → **Delivery consumes it → assigns a rider → publishes `delivery-assigned`**. **Captured live:** order `Confirmed`, rider **Lakshmi** assigned — whoever the query returns first, not a fixed name. The Saga now has 3 participants — see `saga-deep-dive.md` for choreography-vs-orchestration at this scale, and §3.5 below for what happens when the 3rd participant *rejects*.

## 3. Redis-geo live location (ADR-034)

**What you're proving:** live location is a different workload from the order-history you'd store in Postgres — it's a latest-wins overwrite, not a growing table, so it lives in Redis, not the Delivery service's own relational DB.

```bash
curl -s -o /dev/null -X PUT http://localhost:5250/api/v1/deliveries/$ORDER/location -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"latitude":12.95,"longitude":77.64}'
curl -s http://localhost:5250/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $TOKEN"   # location: {latitude:12.95, longitude:77.64}
# agentId is in the track JSON (do not leave a placeholder). PowerShell:
#   $track = curl.exe -s http://localhost:5250/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $TOKEN"
#   # copy agentId from the JSON, then:
docker exec tadka-redis redis-cli GEOPOS delivery:agents PASTE_AGENT_ID   # raw geo; overwrite-latest, sub-ms
```
**Captured live:** `track` returned the exact lat/long just PUT (`12.95..., 77.64...`); `GEOPOS` on the same agent id returned the same coordinates straight out of Redis. Live location is **Redis GEOADD** (overwrites the latest, no growing table); the **assignment/history** is durable in the Delivery Postgres. Polyglot persistence — the right store per workload, same instinct as Day 6's cache.

## 3.5. Restaurant rejects an already-paid order → compensating refund (ADR-045)

**What you're proving:** Day 9's saga compensation only ever ran on a *declined* payment — no money had moved yet, so cancelling the order was the whole fix. Day 11 earns a genuinely harder case: the restaurant can reject an order **after** the customer has already been charged. Cancelling the order alone would leave a `Completed` payment with nobody's money coming back — this section proves the fix, then proves what happens if you skip it.

Restart the monolith with the reject lever on, so every new order gets rejected the moment it's paid:

```bash
Restaurant__AcceptMode=Reject Restaurant__RefundOnReject=true dotnet run --project src/Tadka.Api
```
```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
sleep 10
curl -s http://localhost:5224/api/v1/orders/$ORDER -H "Authorization: Bearer $TOKEN" | sed -E 's/.*"status":"([^"]+)".*/order: \1/'   # Cancelled
curl -s http://localhost:5240/payments/$ORDER -H "Authorization: Bearer $TOKEN"   # {"status":"Refunded", "gatewayReference":"FAKEREF-..."}
```
**Captured live:** order `Cancelled`, payment **`Refunded`** with its own gateway reference (`FAKEREF-BCDDF14CD458` in the run that produced this line) — a *different* reference from the original charge, proving a real second gateway call happened, not a status flip. Flow: `PaymentCompleted` handler sees `AcceptMode=Reject` → `RefundSagaOrchestrator` cancels the order + writes `refund-requested` to the Outbox → Payment's `RefundRequestedConsumer` calls the gateway's `RefundAsync` → publishes `payment-refunded` → the monolith's `PaymentRefundedConsumer` surfaces it on the live-tracking SSE bus. Give it **8-10 seconds**, not 3 — this is a 2-hop Kafka round trip (`refund-requested` out, `payment-refunded` back), the same order of magnitude as Day 9's catch-up demo, not the 1-hop happy path in §2.

Now the break — turn the compensation off and watch the money get stuck:

```bash
Restaurant__AcceptMode=Reject Restaurant__RefundOnReject=false dotnet run --project src/Tadka.Api
```
```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
sleep 10
curl -s http://localhost:5224/api/v1/orders/$ORDER -H "Authorization: Bearer $TOKEN" | sed -E 's/.*"status":"([^"]+)".*/order: \1/'   # Cancelled
curl -s http://localhost:5240/payments/$ORDER -H "Authorization: Bearer $TOKEN"   # {"status":"Completed", ...} — STILL Completed
```
**Captured live:** order `Cancelled`, payment stayed **`Completed`** — money genuinely stuck, forever, until someone flips the lever back and reconciles the order by hand. The monolith's log names this out loud the moment it happens: `Order {id} cancelled after restaurant rejection, but Restaurant:RefundOnReject is OFF — the completed payment is NOT refunded. Money is stuck.` This is the same shape as Day 9's `RefundOnReject`-style demo levers — a real gap, shown on purpose, not hidden behind a passing test.

Restart the monolith with no env overrides (`AcceptMode` defaults to `Auto`) before continuing to the rest of this runbook — every section after this one assumes orders confirm normally.

## 4. Fault isolation — kill Delivery (ADR-033)

**What you're proving:** Delivery is its own failure domain, same as Payment was on Day 8 — killing it degrades exactly one thing (live tracking) and nothing else.

```bash
# Ctrl+C the Delivery service, then:
curl -s -o /dev/null -w "POST /orders: %{http_code}\n" -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY"   # 201
curl -s -o /dev/null -w "menu: %{http_code}\n" http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu   # 200
```
**Captured live: 201, then 200.** Delivery down → ordering, payment, browsing all unaffected (only live tracking degrades). 3rd independent failure domain. (And like Day 9: the `order-confirmed` messages wait in Kafka → on restart, Delivery catches up and assigns the riders — restart it now before the next section.)

## 4.5. The live-tracking stream needs the same ownership check as REST (ADR-031)

**What you're proving:** Day 10 taught resource ownership as a REST-endpoint rule (`order.CustomerId == User.UserId()`). Day 11 opened a *new* kind of endpoint — a Server-Sent Events stream — and, before this branch's `d2bb1c5` fix, that check had been forgotten on it: any logged-in customer could stream **any** order's live status (and, on this branch, the rider's live GPS riding along with it) just by guessing a guid. The lesson: an ownership check is per-endpoint, not something you learn once and get for free everywhere.

```bash
# Priya (the order's owner) opens her own stream — starts receiving events immediately:
curl -s -N http://localhost:5224/api/v1/orders/$ORDER/events -H "Authorization: Bearer $TOKEN"   # event: Confirmed ... (Ctrl+C to stop)
```
```bash
RAHUL=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"rahul@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
curl -s -o /dev/null -w "Rahul streams Priya's order: %{http_code}\n" http://localhost:5224/api/v1/orders/$ORDER/events -H "Authorization: Bearer $RAHUL"   # 403
```
**Captured live:** Priya's own stream opens and pushes `event: Confirmed`; Rahul's attempt on the same order id returns **403** before the stream ever opens. Same rule as `OrdersController.GetById`, now also enforced in `OrderTrackingController.GetEvents` — see Wiring Reference below.

## 5. API gateway — one entry point (ADR-035)

**What you're proving:** the mobile client shouldn't need to know there are 3 backend hosts. One host, `:8080`, routes by path — and per-service auth still holds underneath, so the gateway is a router, not a trust boundary.

```bash
# Everything through ONE host (:8080). YARP routes by path; per-service JWT is still enforced underneath.
curl -s -o /dev/null -w "login via gateway: %{http_code}\n" -X POST http://localhost:8080/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"priya@tadka.test","password":"Password123!"}'   # 200 → monolith
curl -s -o /dev/null -w "restaurants via gateway: %{http_code}\n" http://localhost:8080/api/v1/restaurants                                   # 200 → monolith
curl -s -o /dev/null -w "payment via gateway (no token): %{http_code}\n" http://localhost:8080/api/v1/payments/$ORDER                        # 401 → payment (per-service auth, even through the gateway)
curl -s -o /dev/null -w "delivery via gateway: %{http_code}\n" http://localhost:8080/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $TOKEN"   # 200 → delivery
```
**Captured live: 200 / 200 / 401 / 200.** Login and browsing route to the monolith; payment through the gateway with no token is still 401 — per-service auth held, even through the front door. Delivery routes correctly with the token. Routing: `/api/v1/payments/**`→payment (path transformed to `/payments/**`), `/api/v1/deliveries/**`→delivery, everything else→monolith. **Edge rate-limit** (fixed window per IP, tune `Gateway:RateLimitPerMinute`): a burst past the limit → **429**. The gateway is a **thin edge** (routing + rate-limit) — **not** a trust boundary; each service still validates the JWT (ADR-031). One more honest edge case worth showing: kill Delivery and hit its route through the gateway — you get **502**, plainly, because the target is down. A single YARP instance is itself a new single point of failure the 3-separate-hosts world didn't have; production runs it behind its own load balancer (Day 12's cloud map).

## 6. PgBouncer — the Day-5 promise comes due (ADR-015 landed)

Day 5 showed one `Tadka.Api` instance can't truly exhaust a pool on a laptop. Day 11 is the first day with **2+ instances**, so this is where the real demo lands.

```bash
# Two instances, both DIRECT to Postgres :5432, each with Maximum Pool Size=60 (2×60=120 > max_connections=100):
ASPNETCORE_URLS=http://localhost:5226 ConnectionStrings__TadkaDb="Host=localhost;Port=5432;Database=tadka;Username=tadka;Password=tadka_local;Minimum Pool Size=5;Maximum Pool Size=60" dotnet run --project src/Tadka.Api --no-launch-profile &
ASPNETCORE_URLS=http://localhost:5227 ConnectionStrings__TadkaDb="Host=localhost;Port=5432;Database=tadka;Username=tadka;Password=tadka_local;Minimum Pool Size=5;Maximum Pool Size=60" dotnet run --project src/Tadka.Api --no-launch-profile &
```
> **`--no-launch-profile` matters here** — without it, `dotnet run` applies `launchSettings.json`'s own `applicationUrl` (`:5224`) *after* your `ASPNETCORE_URLS`, and both instances silently try to bind the same port and crash. This bit us verifying this exact runbook.

```bash
pwsh docs/demo-scripts/02-pgbouncer-connection-exhaustion.ps1 -Urls "http://localhost:5226","http://localhost:5227" -Label "DIRECT :5432" -RequestsPerInstance 150
# -> some requests fail: Npgsql "sorry, too many clients already" (real Postgres connection-limit error)

# Restart BOTH instances pointed at PgBouncer :6432 instead. In transaction-pooling mode, a
# physical connection can be handed to a different client between statements, so .NET's OWN
# client-side pooling must be turned off (Pooling=false) — otherwise Npgsql may try to reuse
# session state PgBouncer has already wiped, producing confusing EF Core errors on anything
# beyond a stateless read (see docs/database/connection-pooling-guide.md):
ASPNETCORE_URLS=http://localhost:5226 ConnectionStrings__TadkaDb="Host=localhost;Port=6432;Database=tadka;Username=tadka;Password=tadka_local;Pooling=false" dotnet run --project src/Tadka.Api --no-launch-profile &
ASPNETCORE_URLS=http://localhost:5227 ConnectionStrings__TadkaDb="Host=localhost;Port=6432;Database=tadka;Username=tadka;Password=tadka_local;Pooling=false" dotnet run --project src/Tadka.Api --no-launch-profile &

pwsh docs/demo-scripts/02-pgbouncer-connection-exhaustion.ps1 -Urls "http://localhost:5226","http://localhost:5227" -Label "VIA PGBOUNCER :6432" -RequestsPerInstance 150
# -> 0 failures. Proof of multiplexing:
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT count(*) FROM pg_stat_activity WHERE usename='tadka';"
```
> **Captured (real run):** direct-to-Postgres — **297/300 succeeded, 3 failed**, every failure a genuine `500` carrying Npgsql's own `"sorry, too many clients already"` (an honest ~1% failure rate, not inflated for effect — the exact count varies a little run to run, since it depends on how the OS schedules 300 concurrent connection attempts against a 100-connection ceiling). Via PgBouncer — **300/300, twice in a row, 0 failures**; `pg_stat_activity` showed **13 real physical connections** servicing both instances' client-side pools (each configured for up to 60 — 120 potential, never anywhere near used). The qualitative story — zero Postgres-level errors through the pooler vs. real ones without it, plus the client-vs-backend connection-count gap — is the lesson, not the exact failure count. **Note:** the captured run above used the same `Maximum Pool Size=60` connection string on both legs for a clean apples-to-apples comparison; `Pooling=false` is the config a real service should ship with once it's actually behind PgBouncer, per `connection-pooling-guide.md` — mention this distinction if a student asks why the demo command differs from the production one. Full numbers: ADR-015.

## 7. Run the tests
```bash
dotnet test    # 47/47 — monolith 35 + Payment 6 + Delivery 6.
```
> If you last checked this number before `4a5988e`/`d2bb1c5` landed, you may remember `40/40` — those two commits added `LocationTrackingTests.cs` (3) and `OrderTrackingAuthorizationTests.cs` (4) to the monolith's suite. `dotnet test` is the source of truth, not a number in a doc.

## 8. Wiring Reference & Cross-Stack Architecture

### Where the code lives in Tadka
- **Delivery extraction (ADR-033):** [`src/Tadka.Delivery.Api/DeliveryService.cs`](../../src/Tadka.Delivery.Api/DeliveryService.cs) (assignment: `FirstOrDefaultAsync(a => a.Status == AgentStatus.Available)` — no `ORDER BY distance`, no `GEOSEARCH`, see the honesty note below), [`Messaging/OrderConfirmedConsumer.cs`](../../src/Tadka.Delivery.Api/Messaging/OrderConfirmedConsumer.cs), [`Data/DeliveryDbContext.cs`](../../src/Tadka.Delivery.Api/Data/DeliveryDbContext.cs) (own Postgres, own migration history, ADR-026 pattern reused).
- **Redis-geo (ADR-034):** [`src/Tadka.Delivery.Api/LocationStore.cs`](../../src/Tadka.Delivery.Api/LocationStore.cs) (`GEOADD`/`GEOPOS`), [`OrderTrackingPublisher.cs`](../../src/Tadka.Delivery.Api/OrderTrackingPublisher.cs) (re-publishes location pings onto the Day-6 SSE backplane, ADR-020/036).
- **API gateway (ADR-035):** [`src/Tadka.Gateway/Program.cs`](../../src/Tadka.Gateway/Program.cs) — YARP route table + fixed-window per-IP rate limiter.
- **Refund saga (ADR-045):** [`src/Tadka.Api/Domain/Restaurants/RestaurantAcceptanceOptions.cs`](../../src/Tadka.Api/Domain/Restaurants/RestaurantAcceptanceOptions.cs) (the `AcceptMode`/`RefundOnReject` levers), [`Infrastructure/Messaging/RefundSagaOrchestrator.cs`](../../src/Tadka.Api/Infrastructure/Messaging/RefundSagaOrchestrator.cs), [`Infrastructure/Messaging/PaymentRefundedConsumer.cs`](../../src/Tadka.Api/Infrastructure/Messaging/PaymentRefundedConsumer.cs) (monolith side); [`src/Tadka.Payment.Api/Messaging/RefundRequestedConsumer.cs`](../../src/Tadka.Payment.Api/Messaging/RefundRequestedConsumer.cs), `PaymentService.RefundAsync` (Payment side).
- **SSE ownership fix (ADR-031):** [`src/Tadka.Api/Controllers/OrderTrackingController.cs`](../../src/Tadka.Api/Controllers/OrderTrackingController.cs)'s `GetEvents` — same `IsAdmin() || order.CustomerId == User.UserId()` check `OrdersController.GetById` already used.

### Cross-Stack Implementation Matrix

| Concern | .NET Core (This Repo) | Java (Spring Boot) | Node.js (TypeScript) | Go |
|---|---|---|---|---|
| **Live-location geo store** | `StackExchange.Redis` `GEOADD`/`GEOPOS` | Lettuce/Jedis, identical Redis GEO commands | `ioredis`, identical command surface | `go-redis`, identical command surface |
| **Reverse-proxy gateway** | YARP | Spring Cloud Gateway | Express-gateway / a thin Node proxy | Kong / Envoy / Traefik (any stack) |
| **Cloud-managed gateway equivalent** | — | AWS ALB + API Gateway | Azure App Gateway + APIM | GCP Cloud Load Balancing + Apigee/Kong |
| **Saga orchestration (if 4+ participants)** | MassTransit state machine / `NServiceBus` Saga | Axon `@SagaEventHandler` / Camunda-Zeebe (BPMN) | Temporal (TS SDK) | Temporal (Go SDK) |
| **Connection pooling in front of Postgres** | PgBouncer (Postgres-specific, stack-agnostic) | same PgBouncer process | same PgBouncer process | same PgBouncer process |

**Pattern is language-neutral; the engine matches your stack and flow complexity.** Choreography (what Tadka does, here and through ADR-045) stays a language-neutral "services react to domain events" idea — the same 3-participant Kafka flow is expressible in Spring Kafka, kafkajs, or segmentio/kafka-go with no change in shape.

## 9. Demo vs. Production: named gaps, not overclaimed features

- **Rider assignment is first-available, not nearest.** ADR-034 names `GEOSEARCH` for "nearby agents," but `DeliveryService.cs`'s query has no `ORDER BY distance` and never calls `GEOSEARCH` — whichever `Available` agent the query returns first gets the order, even if a closer rider exists. This is a real, honest gap, not a hidden one: proximity-based dispatch is the natural next step, not yet built. Production systems doing real geospatial dispatch use `GEOSEARCH`/geohash, and at extreme scale, H3/S2 cell indexing.
- **A single gateway instance is a new SPOF.** §5 already showed a 502 when a route's target is down; the gateway itself needs to run behind its own load balancer (2+ instances) in production — a single YARP process replaces "3 hosts to remember" with "1 host that must never go down alone."
- **The refund saga is choreographed, in-process, on Day 11 — both are temporary by design.** ADR-045 explicitly names its own revisit triggers: once Restaurant is extracted (Day 12), the `AcceptMode` decision belongs in `Restaurant.Api` reacting to `order-confirmed`, not inline in the monolith; and a refund that fails at the gateway needs its own failure path + reconciliation job, not yet modelled.

## ✅ Done when
- [ ] Order paid → Confirmed → `track` shows an assigned rider (3-service saga).
- [ ] PUT location → `track` returns it (Redis-geo); `GEOPOS` shows the raw entry.
- [ ] Restaurant reject + refund: `AcceptMode=Reject, RefundOnReject=true` → order Cancelled, payment **Refunded** with a new gateway reference.
- [ ] The break lever: `RefundOnReject=false` → order Cancelled, payment stays **Completed** — money stuck, and the log says so.
- [ ] Delivery **down** → orders 201 + menu 200 (fault isolation).
- [ ] Live-tracking SSE stream: the order's owner gets events; a different customer's token on the same order id gets **403**.
- [ ] All services reachable via **one host** `:8080`; payment-no-token via gateway still **401**; a dead route's target returns **502**.
- [ ] PgBouncer: direct-to-Postgres shows real failures under 2-instance load; via `:6432` shows zero, with a real physical-connection count proving the multiplex.
- [ ] `dotnet test` → **47/47**.

## Troubleshooting
- **No rider assigned:** is the Delivery service up + `tadka-kafka` healthy? Check its log for `OrderConfirmedConsumer subscribed` and `🛵 Order … assigned to rider`. The monolith must publish `order-confirmed` (it does on auto-confirm after payment).
- **track location is null:** PUT a location first; Redis must be up (`Redis` in the Delivery service's `appsettings.Development.json`).
- **Refund never happens / payment stays Completed even with `RefundOnReject=true`:** give it longer — this is a 2-hop Kafka round trip (`refund-requested` then `payment-refunded`), 8-10 seconds is normal, not the ~3 seconds the happy-path saga in §2 needs. If it's still stuck past 20s, check the monolith log for `PaymentRefundedConsumer subscribed to payment-refunded` and Payment's log for `RefundRequestedConsumer subscribed`.
- **SSE stream returns 403 for the order's real owner:** you're testing with the wrong token — re-login and confirm the `sub` claim matches the order's `customerId`, not a stale token from an earlier section.
- **Gateway 502 on a route:** the target service is down — start all three before the gateway demo.
- **Two `Tadka.Api` instances both crash on startup during the PgBouncer demo:** you forgot `--no-launch-profile` — `launchSettings.json`'s `applicationUrl` overrides your `ASPNETCORE_URLS` and both instances fight over `:5224`.
- **PgBouncer connection check fails with "not allowed":** connect as the `tadka` user (set via `ADMIN_USERS` in `docker-compose.yml`), not `postgres` — or just query `pg_stat_activity` directly, as this runbook now does.

➡️ Next (Day 12): extract **Restaurant** → the canonical **4 services + gateway**; **zero-downtime migrations (Expand & Contract)**; and **deploy** — Terraform/ECS + a cloud **ALB / API Gateway** as a black-box (results, not HCL).
