# Tadka Live Order Tracker

A tiny vanilla JS page that shows **Server-Sent Events** order tracking (ADR-020) in a browser instead of `curl -N`.

## Why this exists

The cohort teaches SSE over a Redis pub/sub backplane. Watching JSON in a terminal works, but students remember it better when the status timeline lights up on screen.

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
2. Click **Place demo order**
3. Watch the timeline fill (Created, then payment may push Confirmed async)
4. Click kitchen buttons (owner token is fetched in the background) to advance Preparing → Delivered
5. Each click should appear on the timeline without refreshing
6. **Day 11+** (Delivery + gateway): after **Confirmed**, the **Delivery map** polls `GET /api/v1/deliveries/{orderId}/track` every 2s and moves the rider dot on a Bangalore canvas

## Technical notes

- Uses `fetch()` + stream reader for SSE, not `EventSource`, because the API requires a JWT (`Authorization` header).
- Kitchen PATCH uses **owner1@tadka.test** (RestaurantOwner role). Customers cannot advance status (ADR-031).
- Demo order: Meghana Foods biryani ×2 (same seeded IDs as `k6/lib.js`).

## Files

| File | Role |
|------|------|
| `index.html` | Single page shell |
| `app.js` | Login, place order, SSE parser, kitchen buttons |
| `styles.css` | Dark theme, timeline UI |

No npm, no build step.