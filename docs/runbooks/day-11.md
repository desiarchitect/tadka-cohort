# Day 11 — Runbook: Extract Delivery (3rd service) + the API Gateway

**Branch:** `day-11`  ·  **What's new:** the 3rd service — **`Tadka.Delivery.Api`** (own DB `delivery-db` 5435; **Redis-geo** live location; Kafka-driven assignment, ADR-033/034) — and a **YARP API gateway** (`Tadka.Gateway`, :8080, single entry + edge rate-limit, ADR-035). The order→payment→**delivery** flow is now a **3-participant Saga**. Now 4 services (monolith :5224, payment :5240, delivery :5250) behind one gateway (:8080).

> New here? Read [`README.md`](README.md). Windows PowerShell → `curl.exe`. Demo password `Password123!`. Deep Saga treatment: `cohort-prep/day-11/saga-deep-dive.md`.

## 1. Run it (infra + 3 services + gateway)

```bash
git checkout day-11
docker compose up -d            # + delivery-db (5435); wait for tadka-kafka + delivery-db healthy
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
curl -s http://localhost:5250/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $TOKEN"   # {"agentName":"Lakshmi","status":"Assigned",...}
```
Flow: order Created → paid → **Confirmed** → monolith publishes `order-confirmed` (Outbox→Kafka) → **Delivery consumes it → assigns a rider → publishes `delivery-assigned`**. The Saga now has 3 participants — see `saga-deep-dive.md` for choreography-vs-orchestration at this scale.

## 3. Redis-geo live location (ADR-034)

```bash
curl -s -o /dev/null -X PUT http://localhost:5250/api/v1/deliveries/$ORDER/location -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"latitude":12.95,"longitude":77.64}'
curl -s http://localhost:5250/api/v1/deliveries/$ORDER/track -H "Authorization: Bearer $TOKEN"   # location: {latitude:12.95, longitude:77.64}
docker exec tadka-redis redis-cli GEOPOS delivery:agents <agentId>   # the raw geo entry (overwrite-latest, sub-ms)
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

## 6. Run the tests
```bash
dotnet test    # 36/36 — monolith 28 + Payment 5 + Delivery 3 (assign + idempotent + per-service 401).
```

## ✅ Done when
- [ ] Order paid → Confirmed → `track` shows an assigned rider (3-service saga).
- [ ] PUT location → `track` returns it (Redis-geo); `GEOPOS` shows the raw entry.
- [ ] Delivery **down** → orders 201 + menu 200 (fault isolation).
- [ ] All services reachable via **one host** `:8080`; payment-no-token via gateway still **401**.
- [ ] `dotnet test` → **36/36**.

## Troubleshooting
- **No rider assigned:** is the Delivery service up + `tadka-kafka` healthy? Check its log for `OrderConfirmedConsumer subscribed` and `🛵 Order … assigned to rider`. The monolith must publish `order-confirmed` (it does on auto-confirm after payment).
- **track location is null:** PUT a location first; Redis must be up (`Redis` in the Delivery service's `appsettings.Development.json`).
- **Gateway 502 on a route:** the target service is down — start all three before the gateway demo.

➡️ Next (Day 12): extract **Restaurant** → the canonical **4 services + gateway**; **zero-downtime migrations (Expand & Contract)**; and **deploy** — Terraform/ECS + a cloud **ALB / API Gateway** as a black-box (results, not HCL).
