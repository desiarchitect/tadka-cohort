# TEMPLATE: RUN-AND-TEST.md for [TOY NAME] Toy

**Toy:** [e.g. Stateful WebSocket / Chat System Toy]
**Day Introduced:** [e.g. Day 06 - Cache, Realtime & Hot-Key]
**Related Curriculum:** [link to day plan, option-space, interview-track, domain-primer, or Gemini gap]
**Purpose:** Failure-first demonstration of [the topic]. Show what breaks in a realistic scenario, then the fix, with measurable before/after.

## 1. Overview & Why This Toy Exists
[Explain the system design question it addresses, why it's missing from core Tadka (Tadka is OLTP-focused), and how it supports interview prep or the cohort's "show it, don't just tell it" rule.]

## 2. The Failure Scenario
[Describe the real-world failure: e.g. "In a chat system, naive per-instance WebSocket connections lead to lost messages when scaling horizontally, or memory exhaustion under 10k concurrent connections. What breaks: presence updates fail, offline delivery drops, fan-out becomes O(n) bottleneck."]

Expected bad symptoms: [list metrics like connection count, memory, message loss rate, latency].

## 3. Exact Steps to Induce & Observe the Break
**Prerequisites:** Docker Desktop, Node 20+, (or .NET if applicable), PowerShell or bash.

**On Windows (PowerShell):**
```powershell
# commands to start the broken version
docker compose -f docker-compose.break.yml up -d
node simulate-load.js --clients 10000 --duration 60s
# watch metrics: e.g. tasklist for memory, or console output for dropped messages
```

**On macOS/Linux:**
```bash
docker compose -f docker-compose.break.yml up -d
node simulate-load.js --clients 10000 --duration 60s
```

**Observe:**
- Run the load.
- Note p99 latency or error rate or memory usage.
- [Expected output example]

## 4. The Fix
[Step by step: the architectural pattern, e.g. "Add Redis pub/sub backplane for cross-instance broadcast. Use connection manager for presence. Implement offline queue with TTL."]

**Apply the fix:**
- Change config or run the fixed compose/script: `docker compose -f docker-compose.fixed.yml up -d`
- Or edit the code comment to enable backplane.

## 5. Steps to Verify the Fix
Re-run the exact same load generator.
Compare:
- Before: X messages lost, memory 2GB at 5k clients.
- After: 0 loss, memory stable at 400MB, latency <50ms.

## 6. Full Run Instructions
**Quick smoke (broken then fixed):**
[commands]

**Full demo:**
[more]

**Clean up:**
`docker compose down -v`

**Versions used for this toy:**
- Node: v20
- Docker: 4.30+
- Optional: k6 for load

## 7. Test Cases & Expected Results
| Test | Command/Setup | Broken Result | Fixed Result | Notes |
|------|---------------|---------------|--------------|-------|
| Test 1: Scale to N clients | ... | p99 > 2s, 30% drops | p99 < 100ms, 0 drops | ... |
| Test 2: Offline delivery | ... | messages lost | queued and delivered on reconnect | ... |

## 8. Troubleshooting
- "Port already in use": `docker compose down`
- "Redis not connecting": check docker network, use correct host (redis in compose).
- Numbers don't match README: run in Release mode or with --seed for determinism; note your hardware.
- On Windows: use full powershell, not git bash for some cmds.

## 9. Cross-Stack Notes
- In Java/Spring: Use Redis pub/sub with Spring Data Redis, WebSocket with STOMP over SockJS or native.
- In Go: gorilla/websocket + redis client, or nats for backplane.
- In plain Node: socket.io with redis adapter is common equivalent.
- The core pattern (backplane for broadcast + connection registry) is the same; only the library differs.

## 10. Curriculum Links
- Day 06 plan.md / option-space.md: See the "Realtime delivery to the client" and "Backplane" decisions.
- interview-pack/interview-track.md : Maps to "Chat System (WhatsApp)" drill in Week 5.
- interview-pack/concepts-cheat-sheet.md or distributed-systems-guarantees.md for related primitives (fan-out, at-least-once).
- Suggested note for day plan: "Run the toy in toydemo/day-06-.../ before the SSE/WebSocket discussion to see the failure modes first-hand."

## 11. Failure-First Narrative (for slides/instructor)
"In a real multi-instance chat app, if every server only knows its own connected clients, when user A on server 1 sends to B on server 2, the message is dropped. With 10k concurrent users, a single instance's memory blows up. The fix is a shared backplane (Redis pub/sub) so any server can publish to all interested connections, plus a registry for presence. This is exactly what we see when we run the toy: before the backplane, cross-instance messages are lost and load test shows high memory; after, zero loss and stable."

## 12. Limitations & What This Toy Does Not Cover
- This is a minimal simulation (not production chat with auth, persistence, etc.).
- Does not cover full presence with heartbeats or scaling to millions (use for illustration of the pattern).
- Assumes local Redis; production would use clustered Redis with sentinel.
- No UI/client app included (use curl or simple node client for the load).

**How to contribute updates to this toy:** Update the code, re-capture numbers on a clean run, update this doc, and mark progress in TOY-DEMO-PLAN.md. Always keep the failure-first structure.

---

*This is the template. Duplicate for each toy and fill in specifics. Keep it copy-paste ready for students/instructors.*