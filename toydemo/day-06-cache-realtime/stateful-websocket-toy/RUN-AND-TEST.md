# RUN-AND-TEST.md for Stateful WebSocket / Realtime Toy

**Toy:** Stateful WebSocket / Realtime Toy
**Day Introduced:** Day 06 (realtime — contrasts with Tadka SSE + Redis pub/sub, ADR-020)
**Related Curriculum:** Day 6 SSE backplane, interview-pack WhatsApp/Chat archetype, domain-primers realtime, review.md breadth gaps.
**Purpose:** Failure-first demo of multi-instance WebSocket chat *without* a backplane (~50% messages lost) vs Redis pub/sub fan-out (~100% delivery).

## 1. Overview & Why This Toy Exists
Tadka Day 6 builds **SSE** (server→client) + Redis pub/sub for **order tracking** — one publisher, many listeners. WhatsApp/Chat interviews ask for **bidirectional WebSocket**, **presence**, **ordering**, and **horizontal scale**.

The first scale wound: you run 2+ WS instances behind a load balancer. Room state in memory → messages never cross instances. Users see a "ghost chat" where half the group never gets messages.

## 2. The Failure Scenario
**Workload:** Room `bangalore-foodies` with 100 users, split 50/50 across 2 WS servers. 200 chat messages.

**Naive pattern:** Each server keeps room membership in a local `Map`. Broadcast only to local sockets.

**Bad symptoms:**
- ~50% delivery rate (only same-instance recipients)
- Gets worse with more instances (3 instances ≈ 67% loss)
- "Works in dev" (single instance), fails in prod
- Ops adds instances to handle connections → reliability drops

## 3. Exact Steps to Induce & Observe the Break
```powershell
cd tadka\toydemo\day-06-cache-realtime\stateful-websocket-toy
node index.js --mode=break
```

**Observe:**
- `Delivery rate: 49.5%` (approximately half)
- `Expected deliveries: 19800` vs `Actual: ~9800`

## 4. The Fix
**Redis pub/sub backplane** (same primitive as Tadka `RedisOrderTrackingBus`, applied to bidirectional chat):
- Publish `room:{id}` on send
- Every instance subscribes and fans out to local sockets

```powershell
node index.js --mode=fix
```

**Observe:** `Delivery rate: 100%`

## 5. Steps to Verify the Fix
| Metric | break | fix |
|--------|-------|-----|
| Simulation delivery rate | ~49.5% | 100% |
| real-chat.js (50 clients) | ~45–55% | ~95–100% |

## 6. Full Run Instructions
**Quick smoke:**
```powershell
node index.js --mode=break
node index.js --mode=fix
```

**Real WebSockets + Redis:**
```powershell
docker compose up -d redis   # from tadka/
npm install
node real-chat.js --mode=break
node real-chat.js --mode=fix
```

## 7. Test Cases & Expected Results
| Test | Command | Broken | Fixed |
|------|---------|--------|-------|
| Simulation | `index.js --mode=break` | ~49.5% delivery | — |
| Simulation | `index.js --mode=fix` | — | 100% |
| Real 2-instance chat | `real-chat.js --mode=break` | ~50% | — |
| Real with backplane | `real-chat.js --mode=fix` | — | ~100% |

## 8. Troubleshooting
- **Redis connection refused** — `docker compose up -d redis`
- **real-chat delivery not exactly 50%** — client timing; shape matters
- **EADDRINUSE 31101** — kill prior node processes or change ports in script

## 9. Cross-Stack Notes
- **Tadka ADR-020:** SSE + Redis pub/sub — one-way; same backplane idea.
- **Java:** Spring WebSocket + Redis pub/sub relay.
- **Go:** gorilla/websocket + redis.PSubscribe.
- **WhatsApp interview:** backplane + message queue for offline + sequence numbers per chat.

## 10. Curriculum Links
- ADR-020 live-tracking SSE backplane (contrast: SSE vs WS)
- Day 6 diagram-sse-backplane.md
- Interview-pack WhatsApp / Design Chat
- Day 11 gateway (sticky sessions are a brittle alternative to backplane)

## 11. Failure-First Narrative
"Chat worked perfectly in dev — one Node process. You deploy two instances behind the ALB. Priya and Rahul are in the same group. Priya hits server A, Rahul hits server B. Priya says 'let's order biryani.' Rahul never sees it. Not a bug in your message handler — you never built cross-instance fan-out. SSE for order status is easier: one server publishes. Chat is hard: every user can publish. Fix: Redis pub/sub backplane — publish once, every instance delivers to its local sockets."

## 12. Limitations
- Does not implement presence, read receipts, or per-room ordering guarantees.
- Does not demo sticky sessions (anti-pattern at scale).
- Does not cover WebSocket auth, heartbeats, or connection limits per process.
- Single-room only.

---

*Teaching flow:* simulation for instant 49.5% vs 100% → `real-chat.js` so students see WebSocket frames + Redis fix live.