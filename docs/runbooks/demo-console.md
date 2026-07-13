# Demo Console runbook

The Demo Console (`samples/live-tracker/`, served at **http://localhost:8080/demo/**) is the
visual surface for several break demos. This runbook lists the exact clicks per demo so class
time is never spent hunting for buttons.

Source of truth for the demos themselves: the per-day break kits
(`cohort-prep/day-XX/break-kit-day-XX.md`) and `DEMOS.md`.

## Setup (any day)

```powershell
cd D:\work\desi-architect\tadka
docker compose up -d postgres redis
# start the services for your day branch, gateway last
```

Open http://localhost:8080/demo/ and log in as `priya@tadka.test` / `Password123!`.

## Day 4 - Idempotency break (ADR-011)

Break:
1. Idempotency-Key mode -> **No key (BROKEN)**
2. Click **Double tap**
3. Tap log shows two rows, second one red: `DUPLICATE ORDER - customer pays twice`
4. Prove it in the DB: two `ordering.orders` rows, and (Day 8+) two payment charges

Fix:
1. Mode -> **Same key on every tap**
2. Click **Rotate key** (fresh key), then **Double tap**
3. Tap log shows `201 created` then `200 replay - same order returned` - one order, one charge

## Day 6 - Scale-out state divergence (sessions lesson)

Requires the `scale-out` compose profile (3 monolith replicas behind nginx, Day 6 kit).

1. Stop Redis: `docker compose stop redis` (cache falls back to per-instance memory)
2. Change a price as the owner (or via the Day 6 kit script)
3. Click **Refresh menu** 5-6 times
4. Watch the **instance pill** change and the **price flip-flop** between old and new,
   depending on which replica's private cache answers
5. Start Redis again -> refresh -> every instance agrees

## Day 7/9 - Payment failure visible

1. Set the payment gateway lever to `Failing` (day runbook)
2. Pay -> Payment pill shows **Failed - <reason>**; timeline shows the order cancelled (saga)

## Day 11 - Refund compensation payoff (ADR-052)

1. Set `Restaurant:AcceptMode=Reject` and optionally `Restaurant:RefundOnReject=false` for the money-stuck break (Day 11 kit, ADR-045). Real levers on Tadka.Api — not documentation-only.
2. Pay -> Payment pill goes **Completed** (charge landed)
3. Restaurant rejects -> watch the pill flip to **Refunded** (purple) and the timeline show
   **Cancelled** - the compensation in real time
4. Break variant (compensation disabled): pill stays **Completed** while the order is
   **Cancelled** - money stuck; that inconsistency is the lesson

## Day 11+ - Delivery map

After **Confirmed**, the map card polls the Delivery service and moves the rider dot.
No rider appears if the Delivery service is down - that is itself a demo (fault isolation).
