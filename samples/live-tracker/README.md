# Tadka Demo Console

A vanilla JS page used across the cohort for live demos: menu with server-side prices, a
double-tappable **Pay** button with an idempotency lever, payment status, **Server-Sent Events**
order tracking (ADR-020), and the delivery map (Day 11+).

## Why this exists

Students remember failures they can *see*. The console makes the break demos visceral:

| Demo | Day | What you see |
|------|-----|--------------|
| Double-tap Pay with **No key** | Day 4 | Two orders created - red "DUPLICATE ORDER" rows in the tap log |
| Double-tap Pay with **Same key** | Day 4 | 201 then 200 replay - same order id (ADR-011 fix) |
| Refresh menu with Redis off, 3 instances | Day 6 | Different prices per instance - "state must leave the instance" |
| Instance pill | Day 6 | Which replica answered (`X-Tadka-Instance` header, scale-out profile) |
| Payment pill flips to **Failed** | Day 7/9 | Gateway declined - saga cancels the order |
| Payment pill flips to **Refunded** | Day 11 | Restaurant rejected after charge - compensation refunds |
| Live stream timeline | Day 6+ | SSE over Redis pub/sub, no polling |
| Delivery map | Day 11+ | Rider dot moves via Redis GEO |

## Prerequisites

- Stack running with **Redis** (SSE returns 503 without it)
- **Gateway** on port **8080** (canonical entry point)
- Monolith on 5224 (gateway proxies `/api/v1/*`)

```powershell
cd D:\work\desi-architect\tadka
docker compose up -d postgres redis
# start monolith + gateway per your day branch runbook
```

## Open the demo

With the gateway running:

**http://localhost:8080/demo/**

Static files are copied from this folder into `Tadka.Gateway/wwwroot/demo` at build time.

## Quick class demo

1. Log in as **priya@tadka.test** / `Password123!`
2. Pick a restaurant + menu item (prices are server-side; the client never sends a price)
3. Choose an **Idempotency-Key mode**:
   - *New key per tap* - a normal client; every tap is a distinct order
   - *Same key on every tap* - safe retry; second tap returns 200 with the original order
   - *No key* - the Day 4 break; **Double tap** creates two orders and two charges
4. Click **Pay** (or **Double tap** for the deterministic 2-requests-80ms-apart version)
5. Watch the tap log classify every response: created / replay / DUPLICATE
6. The **Payment** card polls `GET /api/v1/payments/{orderId}` every 2s (Day 8+)
7. Kitchen buttons (owner token fetched in the background) advance Preparing -> Delivered
8. **Day 11+**: after **Confirmed**, the map polls `GET /api/v1/deliveries/{orderId}/track`

Detailed shoot-day steps: `docs/runbooks/demo-console.md`.

## Technical notes

- Uses `fetch()` + stream reader for SSE, not `EventSource`, because the API requires a JWT
  (`Authorization` header).
- The Pay button is intentionally **not disabled while a request is in flight** - the Day 4
  break depends on a real double-tap sending two overlapping POSTs.
- The idempotency lever is client-side only: `Idempotency-Key` is an optional header in the API
  (ADR-011), so "break" simply means not sending it. No server flag needed.
- The instance pill reads the `X-Tadka-Instance` response header when present (Day 6 scale-out
  profile); it shows `n/a` on single-instance days.
- Kitchen PATCH uses **owner1@tadka.test** (RestaurantOwner role). Customers cannot advance
  status (ADR-031).

## Files

| File | Role |
|------|------|
| `index.html` | Single page shell |
| `app.js` | Login, menu, pay + tap log, payment polling, SSE parser, kitchen buttons |
| `styles.css` | Dark theme, timeline + tap log UI |

No npm, no build step.
