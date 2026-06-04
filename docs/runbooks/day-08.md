# Day 8 — Runbook: Extract Payment into its own Service

**Branch:** `day-08`  ·  **What's new:** Payment is now a **separate service** (`Tadka.Payment.Api`) with its **own database** and an **HTTP** bridge (ADR-024/025/026). Day 7 fixed payment's *latency*; Day 8 fixes its *fault/security/data* coupling — a payment fault can no longer take the monolith down. The grep-clean Day-7 seam made this a **move, not a rewrite**. Now two apps + four infra containers (postgres 5432 + replica 5433 + redis 6379 + **payment-db 5434**).

> New here? Read [`README.md`](README.md). Windows PowerShell → use `curl.exe`. Two apps run side by side (no Dockerfiles — compose is infra only).

## 1. Run it (infra + BOTH apps)

```bash
git checkout day-08
docker compose up -d            # postgres(5432) + replica(5433) + redis(6379) + payment-db(5434)
docker compose ps               # all four healthy
```
In **two terminals**:
```bash
# Terminal 1 — the Payment service (own DB on 5434), http://localhost:5240
dotnet run --project src/Tadka.Payment.Api

# Terminal 2 — the monolith, http://localhost:5224
dotnet run --project src/Tadka.Api
```
```bash
curl http://localhost:5240/health        # {"status":"Healthy","service":"payment"}
curl http://localhost:5224/health        # Healthy
```
Each app migrates its **own** database on startup. Two databases now:
```bash
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "\dt payment.*"   # payment.payments + payment.__EFMigrationsHistory
docker exec tadka-postgres   psql -U tadka -d tadka        -c "\dn"              # no 'payment' schema here any more
```

Handy variables:
```bash
RID=a1b2c3d4-0001-4000-8000-000000000001; ITEM=b1b2c3d4-0001-4000-8000-000000000001; CID=c1b2c3d4-0001-4000-8000-000000000001
BODY='{"customerId":"'$CID'","restaurantId":"'$RID'","items":[{"menuItemId":"'$ITEM'","quantity":1}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.9,"longitude":77.7}}'
```

## 2. The shipped path: order settles across the HTTP bridge (ADR-025)

`POST /orders` still returns in **ms** (async preserved); the background processor calls the Payment **service** over HTTP; the order converges to `Confirmed`.
```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY" | sed -E 's/.*"id":"([^"]+)".*/\1/')
sleep 1
curl -s http://localhost:5224/api/v1/orders/$ORDER | sed -E 's/.*"status":"([^"]+)".*/order: \1/'        # Confirmed
curl -s http://localhost:5240/payments/$ORDER                                                            # {"status":"Completed","gatewayReference":"FAKEPAY-…"}
```
> Captured on a dev laptop: `POST /orders` ~tens of ms; converges `Created → Confirmed` in ~hundreds of ms. The payment row lives in the **Payment service's** DB, not the monolith's.

## 3. Fault isolation — kill Payment, the monolith lives (ADR-024)

Stop the Payment service (Ctrl+C in Terminal 1, or kill `:5240`). The monolith keeps serving:
```bash
curl -s -o /dev/null -w "menu: %{http_code}\n"   http://localhost:5224/api/v1/restaurants/$RID/menu     # 200 — unaffected
curl -s -o /dev/null -w "health: %{http_code}\n" http://localhost:5224/health                            # 200 — unaffected
curl -s -o /dev/null -w "POST /orders: %{http_code} in %{time_total}s\n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY"   # 201 in ms
```
> On Day 7 (in-process) a payment fatal shared the monolith's host. Now it doesn't. To see a payment *crash* (not just "stopped"): run the service with `Payment__CrashOnCharge=true`, place an order → the **Payment service** process dies on the charge, but `curl http://localhost:5224/health` is still `200`. (`Environment.FailFast` — demo lever only.)

## 4. The temporal-coupling gap — synchronous HTTP's cost (ADR-025 → earns Day 9)

With the Payment service **still down**, place an order:
```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY" | sed -E 's/.*"id":"([^"]+)".*/\1/')
sleep 3
curl -s http://localhost:5224/api/v1/orders/$ORDER | sed -E 's/.*"status":"([^"]+)".*/order: \1/'        # still Created (PENDING — never settled)
```
> The order was accepted (good — intake is decoupled), but the charge was **lost**: the queued item was consumed and the HTTP call failed. Synchronous HTTP couples caller and callee *in time*. **This is the cliffhanger Day 9 fixes** with Kafka + the Outbox pattern (durable, redelivered, idempotent) — restart the Payment service today and new orders settle again, but the pending one stays lost.

## 5. Own-database isolation (ADR-026)

```bash
docker compose stop payment-db
curl -s -o /dev/null -w "menu with payment-db DOWN: %{http_code}\n" http://localhost:5224/api/v1/restaurants/$RID/menu   # still 200 — the monolith has its OWN DB
docker compose start payment-db
```
> A payment-database outage degrades only payment. On Day 7 (shared Postgres, payment migrated in the monolith's startup) the same outage could stop the whole app from booting.

## 6. The boundary holds — grep proves it (ADR-024)

```bash
# The monolith has NO payment domain/gateway/DbContext — only a typed HTTP client + the shared events:
grep -rn "FakePaymentGateway\|PaymentDbContext\|Domain.Payments" src/Tadka.Api    # nothing
ls src/Tadka.Api/Modules/Payments    # PayForOrderOnOrderPlaced, PaymentProcessor, PaymentWorkChannel, IPaymentClient, HttpPaymentClient, PaymentClientOptions, PaymentClientResilience
```

## 7. Run the tests

```bash
dotnet test      # 25/25 — monolith 21 (19 order-flow/state-machine + 2 payment-reaction) + Payment service 4
```
- Monolith `PaymentReactionTests`: publish `PaymentCompleted`/`PaymentFailed` → order Confirmed/Cancelled (the reaction seam, no network).
- `Tadka.Payment.Api.Tests`: charge over HTTP → Completed; decline → Failed (200, a business outcome); slow gateway + tiny timeout → fast Failed; idempotent → one charge, same reference.

## ✅ Done when

- [ ] `\dt payment.*` exists in **`tadka_payment`** (5434); the monolith's `tadka` DB has **no** payment schema.
- [ ] Shipped: `POST /orders` returns `Created` in ms → converges to `Confirmed`; `GET :5240/payments/{id}` is `Completed`.
- [ ] Payment service **down** → monolith `/health`, menu, and `POST /orders` all still `200`/`201`.
- [ ] Payment down → a new order stays **pending** (the temporal-coupling gap).
- [ ] `payment-db` stopped → monolith menu still `200`.
- [ ] `grep` over the monolith finds no payment domain/gateway/DbContext.
- [ ] `dotnet test` → **25/25**.

## Troubleshooting

- **Order never reaches Confirmed:** is the Payment service up on `:5240`? Check `Payment:ServiceUrl` in the monolith's `appsettings.Development.json`. The monolith log shows `Order … left PENDING — Payment service did not settle it` when it can't reach the service.
- **Payment service won't start:** is `payment-db` (5434) healthy? `docker compose ps`. It owns its own DB.
- **Port in use:** the Payment service uses `:5240` (see `Properties/launchSettings.json`), the monolith `:5224`.
- **Reset everything:** `docker compose down -v && docker compose up -d`, then run both apps (each re-migrates its own DB).

➡️ Next (Day 9): replace the synchronous HTTP bridge with **Kafka + the Outbox pattern** — a down Payment service means messages *wait*, not lost charges; at-least-once delivery + an idempotent consumer; the Saga pattern for the distributed transaction.
