# Day 4 — Runbook: Hardening the Order Flow (retries, races, events)

**Branch:** `day-04`  ·  **What's new:** the Day-3 flow now survives the real world — **idempotency** (safe retries on `POST /orders`), **optimistic concurrency** (`xmin` → `409` on a lost-update race), and **in-process domain events** (side-effects after commit). Plus **HTTP integration tests** (Testcontainers). Still one app + one Postgres.

> New here? Read [`README.md`](README.md). Windows PowerShell → use `curl.exe`.

## 1. Run it

```bash
git checkout day-04
docker compose up -d
dotnet run --project src/Tadka.Api
```

## 2. Idempotency — the double-tap demo (ADR-011)

A flaky network / impatient tap sends `POST /orders` twice. **Without** a key you get two orders:

```bash
BODY='{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"a1b2c3d4-0001-4000-8000-000000000001","items":[{"menuItemId":"b1b2c3d4-0001-4000-8000-000000000001","quantity":2}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.9,"longitude":77.7}}'

curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY" | sed -E 's/.*"id":"([^"]+)".*/order A = \1/'
curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY" | sed -E 's/.*"id":"([^"]+)".*/order B = \1/'
# → TWO different ids = two orders, customer billed twice.
```

**With** an `Idempotency-Key` (reused on the retry) you get **one** order — the replay returns the original (`200`, not a new `201`):

```bash
curl -s -o /dev/null -w "first = %{http_code}\n"  -X POST http://localhost:5224/api/v1/orders \
  -H "Content-Type: application/json" -H "Idempotency-Key: demo-key-123" -d "$BODY"   # 201
curl -s -o /dev/null -w "replay = %{http_code}\n" -X POST http://localhost:5224/api/v1/orders \
  -H "Content-Type: application/json" -H "Idempotency-Key: demo-key-123" -d "$BODY"   # 200 (same order, no duplicate)
```

## 3. Optimistic concurrency — the lost-update demo (ADR-012)

Two people act on the **same** order at the same instant. With the `xmin` concurrency token, they can't both win — one gets `204`, the other **`409 Conflict`** (or `422` if they happen to serialise). Either way, **no lost update**.

```bash
# place an order, grab its id
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY" | sed -E 's/.*"id":"([^"]+)".*/\1/')
# fire two confirmations at once (bash):
curl -s -o /dev/null -w "A=%{http_code} " -X PATCH http://localhost:5224/api/v1/orders/$ORDER/status -H "Content-Type: application/json" -d '{"status":"Confirmed"}' &
curl -s -o /dev/null -w "B=%{http_code}\n" -X PATCH http://localhost:5224/api/v1/orders/$ORDER/status -H "Content-Type: application/json" -d '{"status":"Confirmed"}' &
wait
# → one 204 and one 409 (or 422). Never two 204s.
```
> Status taxonomy: **`409`** = you raced and lost (reload + retry) · **`422`** = the request broke a domain rule (e.g. illegal transition) · **`404`** = gone. The deterministic proof of the `409` mechanism is in the test suite (next step).

## 4. Domain events — side-effects after commit (ADR-013)

Confirm an order and watch the **app log** (the terminal running `dotnet run`):
```bash
curl -s -o /dev/null -X PATCH http://localhost:5224/api/v1/orders/$ORDER/status -H "Content-Type: application/json" -d '{"status":"Confirmed"}'
# app log shows:  📲 Notification: order … confirmed — SMS sent to customer …
```
The notification handler runs **after** the transition is saved — so a failed notification can never roll back a confirmed order.

## 5. Run the tests

```bash
dotnet test
# → Passed! 19 tests (13 state-machine unit + 6 integration). Needs Docker (Testcontainers spins a real Postgres).
```
The integration tests include a **deterministic `xmin` conflict** test (two contexts, stale write → `DbUpdateConcurrencyException`) and an **idempotent-replay** test.

## ✅ Done when

- [ ] No-key double POST → 2 orders; same `Idempotency-Key` → `201` then `200` (one order).
- [ ] Two concurrent confirms → one `204`, one `409`/`422` (no lost update).
- [ ] After a confirm, the app log shows the "📲 Notification …" line.
- [ ] `dotnet test` → **19/19 green**.

## Troubleshooting

- **`dotnet test` hangs/fails to start a container:** Docker Desktop must be running (Testcontainers needs it).
- **Both concurrent PATCH returned `204`:** they serialised — re-run; or trust the deterministic test. (On Windows PowerShell, the `&` background trick differs — use Git Bash, or rely on `dotnet test`.)

➡️ Next: [day-05.md](day-05.md) — make it fast under load (indexes, pool, read replica).
