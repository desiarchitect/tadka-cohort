# Day 7 — Runbook: The Payment Brownout → Isolate It & Earn the Boundary

**Branch:** `day-07`  ·  **What's new:** payment is finally wired in — and it teaches a failure. We reproduce the **payment-gateway brownout** (a slow gateway stalls order placement), then fix it three ways: a **Polly** timeout + bulkhead (ADR-021), a **modular monolith** with **MediatR** (ADR-022 — Payment gets its own `DbContext`, schema, and migration history; Ordering has zero references to it), and **asynchronous payment** off the request path (ADR-023). Same infra as Day 6 (Postgres primary 5432 + replica 5433 + Redis 6379).

> New here? Read [`README.md`](README.md). Windows PowerShell → use `curl.exe` (the `curl` alias is `Invoke-WebRequest`). The brownout levers are set via environment variables, shown below.

## 1. Run it (shipped config = async payment, fast gateway)

```bash
git checkout day-07
docker compose up -d
dotnet run --project src/Tadka.Api      # migrates BOTH contexts: core drops payment.payments; PaymentDbContext recreates it
curl http://localhost:5224/health        # 200 Healthy
```

Two migration histories now exist — proof the module owns its data (ADR-022):
```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "\dt payment.*"
# payment.payments  AND  payment.__EFMigrationsHistory   (core's history stays in public.__EFMigrationsHistory)
```

Handy variables:
```bash
RID=a1b2c3d4-0001-4000-8000-000000000001        # Meghana
ITEM=b1b2c3d4-0001-4000-8000-000000000001       # Chicken Biryani (₹299)
CID=c1b2c3d4-0001-4000-8000-000000000001        # seeded customer
BODY='{"customerId":"'$CID'","restaurantId":"'$RID'","items":[{"menuItemId":"'$ITEM'","quantity":1}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.9,"longitude":77.7}}'
```

## 2. The shipped path: async payment, status converges (ADR-023)

`POST /orders` returns **immediately** as `Created`; the background processor charges the card and the order auto-confirms moments later.
```bash
curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY" | sed -E 's/.*"id":"([^"]+)".*"status":"([^"]+)".*/order \1 returned status=\2/'
# → status=Created  (returned in milliseconds — the gateway was NOT on the request path)
ORDER=<paste the id>
sleep 1
curl -s http://localhost:5224/api/v1/orders/$ORDER | sed -E 's/.*"status":"([^"]+)".*/now: \1/'   # → Confirmed (payment settled in the background)
docker exec tadka-postgres psql -U tadka -d tadka -x -c 'select * from payment.payments order by 1 desc limit 1;'   # Status=Completed, a GatewayReference, CompletedAt set
```
> Measured on a dev laptop: `POST` returns in **~tens of ms**; the order converges `Created → Confirmed` in **~400 ms**. Open the Day-6 SSE stream (`curl -N .../orders/$ORDER/events`) **before** placing the order to watch it move live.

## 3. Reproduce the BROWNOUT (the naive baseline)

Flip payment to the naive design — **synchronous, inside the order request** — and the gateway to a provider having an incident (**slow, 8 s**), with **no timeout** and an **unbounded** bulkhead (simulating "no resilience"). Stop the app and restart it with these env overrides:

```powershell
# Windows PowerShell:
$env:Payment__Mode="Synchronous"; $env:Payment__Gateway__Behavior="Slow"; $env:Payment__Gateway__SlowDelaySeconds="8"
$env:Payment__TimeoutSeconds="30"; $env:Payment__MaxConcurrentCharges="1000"
dotnet run --project src/Tadka.Api
```
```bash
# bash: Payment__Mode=Synchronous Payment__Gateway__Behavior=Slow Payment__TimeoutSeconds=30 Payment__MaxConcurrentCharges=1000 dotnet run --project src/Tadka.Api
```
Time a single order:
```bash
curl -s -o /dev/null -w "POST /orders took %{time_total}s\n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY"
# → ~9 s.  Every order is now hostage to the payment provider's latency. Under load this drains the
#   primary connection pool (ADR-015) and order placement collapses for everyone. THIS is the brownout.
```
> Captured: **~9.2 s** per order. (Headline symptom = latency/throughput collapse on the order path. The pool-drain contagion to *other* endpoints is the multi-instance amplifier — same honest caveat as Day 5's pool demo.)

## 4. Fix #1 — Polly timeout + bulkhead (ADR-021)

Keep payment synchronous (worst case) but give Polly a **2 s timeout** and a **bounded bulkhead**. A slow gateway now fails **fast and contained** instead of hanging 8 s. Restart:
```powershell
$env:Payment__Mode="Synchronous"; $env:Payment__Gateway__Behavior="Slow"; $env:Payment__TimeoutSeconds="2"; $env:Payment__MaxConcurrentCharges="10"
dotnet run --project src/Tadka.Api
```
```bash
curl -s -o /dev/null -w "POST /orders took %{time_total}s\n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY"
# → ~2 s (warm). The charge is abandoned at the deadline → payment Failed → order Cancelled.
docker exec tadka-postgres psql -U tadka -d tadka -x -c 'select * from payment.payments order by 1 desc limit 1;'
# Status=Failed, FailureReason="TimeoutRejectedException: ... '00:00:02'"
```
> Captured: **9.2 s → ~2.1 s** (warm). Fail-fast trades a slow success for a fast, *handled* failure (the order cancels cleanly instead of the whole app stalling). The bulkhead caps how many charges can be in flight, so a sick gateway can't consume more than N slots.

## 5. Fix #2 — async payment: take it off the request path entirely (ADR-023)

The real fix: order creation shouldn't wait for the bank **at all**. Back to the shipped config (`Payment:Mode=Async`, fast gateway) — restart with **no env overrides** (or set `$env:Payment__Mode="Async"`). Now even with a **slow** gateway, `POST /orders` returns instantly:
```powershell
$env:Payment__Mode="Async"; $env:Payment__Gateway__Behavior="Slow"; $env:Payment__TimeoutSeconds="2"
dotnet run --project src/Tadka.Api
```
```bash
curl -s -o /dev/null -w "POST /orders took %{time_total}s\n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY"
# → milliseconds, even though the gateway is slow. Payment happens in the background; with a slow+2s-timeout
#   gateway the order will end up Cancelled, but ORDER INTAKE NEVER STALLS. Orders keep flowing through the incident.
```
> This is why Swiggy/Zomato show "order placed" instantly and surface payment a moment later. The in-memory queue is the right-sized step; its durable successor is **Kafka + Outbox (Week 5)**.

## 6. A declined payment cancels the order (ADR-023)

```powershell
$env:Payment__Mode="Async"; $env:Payment__Gateway__Behavior="Failing"
dotnet run --project src/Tadka.Api
```
```bash
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY" | sed -E 's/.*"id":"([^"]+)".*/\1/')
sleep 1
curl -s http://localhost:5224/api/v1/orders/$ORDER | sed -E 's/.*"status":"([^"]+)".*/status: \1/'   # → Cancelled (PaymentFailed → order.Cancel via the shared event)
```

## 7. The module boundary — grep proves it (ADR-022)

```bash
# Ordering must have ZERO references to Payment. This returns nothing:
grep -rn "Modules.Payments\|Domain.Payments\|PaymentDbContext" src/Tadka.Api/Domain/Orders src/Tadka.Api/Controllers/OrdersController.cs
```
Ordering raises `OrderPlaced`; the Payment module reacts via a MediatR handler; Payment publishes `PaymentCompleted`/`PaymentFailed`; Ordering reacts. Neither references the other — they share only the event contract (`Domain/Common/Events`). That seam is what Day 8 extracts into a separate service.

## 8. Run the tests

```bash
dotnet test      # 23/23 (19 from Days 1–6 + 4 new payment tests). The order-flow suite runs Payment in
                 # "Off" mode (deterministic Day-4 semantics); PaymentModuleIntegrationTests drives
                 # PaymentService directly: success→confirm, decline→cancel, Polly timeout fail-fast, idempotent one-charge.
```

## ✅ Done when

- [ ] `\dt payment.*` shows both `payment.payments` and `payment.__EFMigrationsHistory` (the module owns its migrations).
- [ ] Shipped (async/fast): `POST /orders` returns `Created` in ms; the order converges to `Confirmed`; payment row is `Completed`.
- [ ] Brownout (sync/slow/no-timeout): `POST /orders` ≈ 9 s.
- [ ] Polly (sync/slow/2 s): `POST /orders` ≈ 2 s; payment `Failed` with `TimeoutRejectedException`; order `Cancelled`.
- [ ] Async (slow gateway): `POST /orders` returns in ms — intake never stalls.
- [ ] `Failing` gateway → order ends `Cancelled`.
- [ ] The grep over Ordering returns nothing.
- [ ] `dotnet test` → **23/23**.

## Troubleshooting

- **`POST` returned 500 in Synchronous mode:** make sure you're on the committed `day-07` (the event-publish path snapshots events before dispatch to survive the synchronous re-entrant payment chain).
- **Env override didn't take:** double-underscore is the .NET nesting separator (`Payment__Gateway__Behavior`). In PowerShell each `$env:` lasts for that shell; open a fresh shell to reset to the shipped `appsettings.Development.json` (async/fast).
- **Payment row not appearing in async mode:** the background processor charges ~immediately, but give it a beat (`sleep 1`) before querying. Check the app log for `💳 Payment COMPLETED` / `❌ Payment FAILED`.
- **Reset everything:** `docker compose down -v && docker compose up -d`, then `dotnet run` (re-applies both migration histories).

➡️ Next (Day 8): extract the Payment module into a **separate service** — its own database and an HTTP bridge (why HTTP before Kafka). The boundary you can already grep for becomes a network boundary.
