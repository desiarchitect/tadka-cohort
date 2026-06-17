# RUN-AND-TEST.md for Rate Limiter Algorithms Toy

**Toy:** Rate Limiter Algorithms Toy
**Day Introduced:** Day 06 (Redis, gateway rate limits, hot-path protection)
**Related Curriculum:** Day 6 plan (cache + SSE), Day 11 gateway edge rate-limit (ADR-035), option-space.md rate-limiting tables, interview-pack "design a rate limiter" questions.
**Purpose:** Failure-first demo of the fixed-window boundary burst flaw (2× traffic at reset) vs token-bucket smooth limiting.

## 1. Overview & Why This Toy Exists
Every backend engineer implements rate limiting. The naive fixed-window counter *looks* correct ("100 per second") but allows **200 requests in 100ms** if the client times a burst across the window boundary. Interviewers love this trap.

This toy makes it concrete with deterministic arrival times, then optional real HTTP 200/429 responses.

## 2. The Failure Scenario
**Workload:** Menu browse endpoint limited to 100 req/s.

**Naive pattern:** Fixed window counter — reset `count=0` every 1000ms.

**Attack:** Send 75 requests at t=950ms (still in window 0) + 75 at t=1050ms (window 1). Each window allows 75 → **150 pass** despite the "100/s" policy. A tighter burst (100+100) passes **200**.

**Bad symptoms:** Gateway protection fails under timed bursts; DB/Redis still sees spikes; SLO violations at predictable boundaries.

## 3. Exact Steps to Induce & Observe the Break
**Prerequisites:** Node.js v18+. No Docker.

```powershell
cd tadka\toydemo\day-06-cache-realtime\rate-limiter-toy
node index.js --mode=break
```

**Observe:**
- `Allowed: 150` (all requests pass)
- `Per-window: {"0":75,"1":75}` — both windows under the 100 cap individually, but combined exceeds intent

## 4. The Fix
**Token bucket:** Tokens refill continuously at the configured rate. Bucket capacity absorbs small bursts, but there is no hard reset to double-dip.

```powershell
node index.js --mode=fix
```

**Observe:**
- `Allowed: ~110`, `Rejected: ~40` (exact numbers depend on refill timing)
- Second window cannot start fresh at full capacity

## 5. Steps to Verify the Fix
| Metric | break (fixed window) | fix (token bucket) |
|--------|----------------------|---------------------|
| Allowed (150 burst) | 150 | ~110 |
| Rejected | 0 | ~40 |
| Boundary exploit | Yes | No |

**Real HTTP verification (two terminals):**

Terminal 1:
```powershell
node server.js --algorithm=fixed-window
```

Terminal 2:
```powershell
node load-client.js --algorithm=fixed-window
```

Repeat with `token-bucket` on both. Fixed window typically allows more 200s at the boundary burst; token bucket returns more 429s.

## 6. Full Run Instructions
**Quick smoke:**
```powershell
node index.js --mode=break
node index.js --mode=fix
```

**Real HTTP (two terminals, zero npm deps):**
```powershell
# Terminal 1
node server.js --algorithm=fixed-window
# Terminal 2
node load-client.js --algorithm=fixed-window

# Terminal 1 — restart with fix
node server.js --algorithm=token-bucket
# Terminal 2
node load-client.js --algorithm=token-bucket
```

**Versions:** Node v18+, no Docker required.

## 7. Test Cases & Expected Results
| Test | Command | Broken | Fixed |
|------|---------|--------|-------|
| Simulation boundary burst | `index.js --mode=break/fix` | 150 allowed | ~110 allowed, ~40 rejected |
| HTTP fixed window | server + load-client `fixed-window` | High 200 count | — |
| HTTP token bucket | server + load-client `token-bucket` | — | More 429s, fewer 200s |

## 8. Troubleshooting
- **ECONNREFUSED on load-client** — Start `server.js` first in another terminal.
- **Port 31091 in use** — `$env:PORT=31092` on both server and client.
- **HTTP numbers differ from simulation** — Real burst timing varies; the *shape* (fixed allows more) is what matters.

## 9. Cross-Stack Notes
- **.NET / Tadka Day 11:** YARP edge rate limiting; Redis sliding window for distributed counts.
- **Java/Spring:** Bucket4j, Resilience4j rate limiter.
- **Go:** `golang.org/x/time/rate` (token bucket).
- **Redis:** `INCR` + `EXPIRE` fixed window is the distributed version of the same flaw — use sliding window log or Redis Cell/GCRA.

## 10. Curriculum Links
- Day 6 — protecting hot paths before/alongside Redis cache.
- Day 11 ADR-035 — gateway edge rate-limit.
- Interview: "Design a rate limiter" → fixed window flaw → token bucket / sliding window → Redis for multi-instance.

## 11. Failure-First Narrative
"You set a limit: 100 requests per second. You deploy a fixed-window counter. A client sends 100 requests at 999ms and another 100 at 1001ms. Your limiter says yes to all 200 — because each second individually looked fine. Your database didn't think they were fine. The fix is continuous refill: token bucket or sliding window. And behind a load balancer, in-memory counters multiply the problem — you need a shared store like Redis."

## 12. Limitations
- Does not implement sliding window log or leaky bucket (stretch goals).
- Does not demo distributed multi-instance counters (mentioned in narrative only).
- HTTP load client uses parallel requests, not sub-ms timed arrivals like the simulation.

---

*Teaching flow:* simulation first (instant numbers), then live HTTP 429s so students hear the limiter click.