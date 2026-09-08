# Day 11 — Runbook: Extract Delivery (3rd service) + the API Gateway

**Branch:** `day-11`  ·  **What's new:** the 3rd service — **`Tadka.Delivery.Api`** (own DB `delivery-db` 5435; **Redis-geo** live location; Kafka-driven assignment, ADR-033/034) — and a **YARP API gateway** (`Tadka.Gateway`, :8080, single entry + edge rate-limit, ADR-035). The order→payment→**delivery** flow is now a **3-participant Saga**. **3 services + gateway** (monolith :5224, payment :5240, delivery :5250, YARP :8080). Restaurant is still in the monolith — the 4th service is Day 12. Also: **PgBouncer** (`:6432`, ADR-015 landed) — the Day-5 pool-exhaustion promise, paid off now that 2+ app instances actually exist.

> New here? Read [`README.md`](README.md). Windows PowerShell → `curl.exe`. Demo password `Password123!`. Deep Saga treatment: `cohort-prep/day-11/saga-deep-dive.md`.

## 1. Run it (infra + 3 services + gateway)

```bash
git checkout day-11
docker compose up -d            # + delivery-db (5435), pgbouncer (6432); wait for tadka-kafka + delivery-db healthy
dotnet run --project src/Tadka.Payment.Api     # :5240
dotnet run --project src/Tadka.Delivery.Api    # :5250 (consumes order-confirmed; seeds riders)
dotnet run --project src/Tadka.Api             # :5224
dotnet run --project src/Tadka.Gateway         # :8080 (the single entry point)
```

## 2. The 3-service Saga: order → payment → delivery (ADR-029/033)

```bash
TOKEN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"priya@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
BODY='{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"a1b2c3d4-0001-4000-8000-000000000001","items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":1}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.93,"longitude":77.61}}'
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
sleep 3
curl -s http://localhost:5224/api/v1/orders/$ORDER -H "Authorization: Bearer $TOKEN" | sed -E 's/.*"status":"([^"]+)".*/order: \1/'   # Confirmed (payment saga)
curl -s http://localhost:5250/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $TOKEN"   # {"agentName":"<first available>","status":"Assigned",...}
# Assignment is FirstOrDefault(Available) — NOT nearest / GEOSEARCH. Whoever is Available wins. Reset agents if a prior run left them OnDelivery.
```
Flow: order Created → paid → **Confirmed** → monolith publishes `order-confirmed` (Outbox→Kafka) → **Delivery consumes it → assigns a rider → publishes `delivery-assigned`**. The Saga now has 3 participants — see `saga-deep-dive.md` for choreography-vs-orchestration at this scale.

## 3. Redis-geo live location (ADR-034)

```bash
curl -s -o /dev/null -X PUT http://localhost:5250/api/v1/deliveries/$ORDER/location -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"latitude":12.95,"longitude":77.64}'
curl -s http://localhost:5250/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $TOKEN"   # location: {latitude:12.95, longitude:77.64}
# agentId is in the track JSON (do not leave a placeholder). PowerShell:
#   $track = curl.exe -s http://localhost:5250/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $TOKEN"
#   # copy agentId from the JSON, then:
docker exec tadka-redis redis-cli GEOPOS delivery:agents PASTE_AGENT_ID   # raw geo; overwrite-latest, sub-ms
```
> Live location is **Redis GEOADD** (overwrites the latest, no growing table); the **assignment/history** is durable in the Delivery Postgres. Polyglot persistence — the right store per workload.

## 4. Fault isolation — kill Delivery (ADR-033)

```bash
# Ctrl+C the Delivery service, then:
curl -s -o /dev/null -w "POST /orders: %{http_code}\n" -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY"   # 201
curl -s -o /dev/null -w "menu: %{http_code}\n" http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu   # 200
```
> Captured: Delivery down → ordering, payment, browsing all unaffected (only live tracking degrades). 3rd independent failure domain. (And like Day 9: the `order-confirmed` messages wait in Kafka → on restart, Delivery catches up and assigns the riders.)

## 5. API gateway — one entry point (ADR-035)

```bash
# Everything through ONE host (:8080). YARP routes by path; per-service JWT is still enforced underneath.
curl -s -o /dev/null -w "login via gateway: %{http_code}\n" -X POST http://localhost:8080/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"priya@tadka.test","password":"Password123!"}'   # 200 → monolith
curl -s -o /dev/null -w "restaurants via gateway: %{http_code}\n" http://localhost:8080/api/v1/restaurants                                   # 200 → monolith
curl -s -o /dev/null -w "payment via gateway (no token): %{http_code}\n" http://localhost:8080/api/v1/payments/$ORDER                        # 401 → payment (per-service auth, even through the gateway)
curl -s -o /dev/null -w "delivery via gateway: %{http_code}\n" http://localhost:8080/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $TOKEN"   # 200 → delivery
```
Routing: `/api/v1/payments/**`→payment (path transformed to `/payments/**`), `/api/v1/deliveries/**`→delivery, everything else→monolith. **Edge rate-limit** (fixed window per IP, tune `Gateway:RateLimitPerMinute`): a burst past the limit → **429**. The gateway is a **thin edge** (routing + rate-limit) — **not** a trust boundary; each service still validates the JWT (ADR-031).

## 6. PgBouncer — the Day-5 promise comes due (ADR-015 landed)

Day 5 showed one `Tadka.Api` instance can't truly exhaust a pool on a laptop. Day 11 is the first day with **2+ instances**, so this is where the real demo lands.

```bash
# Two instances, both DIRECT to Postgres :5432, each with Maximum Pool Size=60 (2×60=120 > max_connections=100):
ASPNETCORE_URLS=http://localhost:5224 ConnectionStrings__TadkaDb="Host=localhost;Port=5432;Database=tadka;Username=tadka;Password=tadka_local;Minimum Pool Size=5;Maximum Pool Size=60" dotnet run --project src/Tadka.Api &
ASPNETCORE_URLS=http://localhost:5225 ConnectionStrings__TadkaDb="Host=localhost;Port=5432;Database=tadka;Username=tadka;Password=tadka_local;Minimum Pool Size=5;Maximum Pool Size=60" dotnet run --project src/Tadka.Api &

pwsh docs/demo-scripts/02-pgbouncer-connection-exhaustion.ps1 -Urls "http://localhost:5224","http://localhost:5225" -Label "DIRECT :5432" -RequestsPerInstance 150
# -> some requests fail: Npgsql "sorry, too many clients already" (real Postgres connection-limit error)

# Now point BOTH instances at PgBouncer :6432 instead (same Maximum Pool Size=60) and repeat:
pwsh docs/demo-scripts/02-pgbouncer-connection-exhaustion.ps1 -Urls "http://localhost:5224","http://localhost:5225" -Label "VIA PGBOUNCER :6432" -RequestsPerInstance 150
# -> 0 failures. Proof of multiplexing:
PGPASSWORD=tadka_local docker exec tadka-postgres psql -U tadka -h tadka-pgbouncer -p 5432 -d pgbouncer -c "SHOW POOLS;"
```
> **Captured (real run):** direct-to-Postgres — **296/300 succeeded, 4 failed** with `"sorry, too many clients already"` (an honest ~1.3% failure rate, not inflated for effect). Via PgBouncer — **300/300, twice in a row, 0 failures**; `SHOW POOLS` showed **~110 client-side connections multiplexed onto ~14-16 physical backend connections**. The qualitative story — zero Postgres-level errors through the pooler vs. real ones without it, plus the client-vs-backend connection-count gap — is the lesson, not the exact failure count. Full numbers: ADR-015.

## 7. Run the tests
```bash
dotnet test    # 40/40 — monolith 31 + Payment 6 + Delivery 3.
```

## ✅ Done when
- [ ] Order paid → Confirmed → `track` shows an assigned rider (3-service saga).
- [ ] PUT location → `track` returns it (Redis-geo); `GEOPOS` shows the raw entry.
- [ ] Delivery **down** → orders 201 + menu 200 (fault isolation).
- [ ] All services reachable via **one host** `:8080`; payment-no-token via gateway still **401**.
- [ ] PgBouncer: direct-to-Postgres shows real failures under 2-instance load; via `:6432` shows zero, with `SHOW POOLS` proving the multiplex.
- [ ] `dotnet test` → **40/40**.

## Troubleshooting
- **No rider assigned:** is the Delivery service up + `tadka-kafka` healthy? Check its log for `OrderConfirmedConsumer subscribed` and `🛵 Order … assigned to rider`. The monolith must publish `order-confirmed` (it does on auto-confirm after payment).
- **track location is null:** PUT a location first; Redis must be up (`Redis` in the Delivery service's `appsettings.Development.json`).
- **Gateway 502 on a route:** the target service is down — start all three before the gateway demo.
- **PgBouncer `SHOW POOLS` fails with "not allowed":** connect as the `tadka` user (set via `ADMIN_USERS` in `docker-compose.yml`), not `postgres`.

➡️ Next (Day 12): extract **Restaurant** → the canonical **4 services + gateway**; **zero-downtime migrations (Expand & Contract)**; and **deploy** — Terraform/ECS + a cloud **ALB / API Gateway** as a black-box (results, not HCL).
