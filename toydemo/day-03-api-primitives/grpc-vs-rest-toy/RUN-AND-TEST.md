# RUN-AND-TEST.md for gRPC vs REST Internal Calls Toy

**Toy:** gRPC vs REST Internal Calls Toy
**Day Introduced:** Day 03/04 (API design, contract primitives, internal vs external API style)
**Related Curriculum:** Day 3 api-style-selection.md, option-space.md (REST vs gRPC for internal hops), interview-pack internal-service-call questions, Gemini-style reviews (gRPC as missing hands-on breadth).
**Purpose:** Failure-first demonstration of why chatty REST between microservices hurts (latency stacks, payload bloat) and how a single gRPC unary batch with protobuf fixes it for internal calls.

## 1. Overview & Why This Toy Exists
Day 3 teaches API contracts and when to pick REST vs gRPC. Students often hear "gRPC is faster" but have never *felt* chatty REST die on an internal hot path.

This toy simulates Ordering → Restaurant: fetch 200 menu items to price one order.

- **Break:** 200 sequential `GET /api/v1/menu-items/{id}` — classic N+1 internal HTTP.
- **Fix:** One `GetMenuItemsBatch` gRPC unary RPC with a `.proto` contract.

Two layers (same pattern as cursor-pagination-toy):
1. **Fast simulation** (`index.js`) — zero deps, instant, teaches the shape.
2. **Real servers** (`real-bench.js`) — actual HTTP + gRPC on localhost, measured wall time.

## 2. The Failure Scenario
**Workload:** Order placement needs 200 menu item records (name, price, category, availability, nutrition metadata).

**Naive REST internal pattern:**
- Loop: `GET /api/v1/menu-items/1`, `GET .../2`, ... `GET .../200`.
- Each call pays HTTP overhead + JSON serialization + connection scheduling.
- Under load, the caller's HTTP client pool becomes the bottleneck.

**Expected bad symptoms:**
- Latency grows linearly with item count (200 × RTT minimum).
- High bytes on the wire (verbose JSON field names on every object).
- Connection pool exhaustion when many orders price concurrently.
- Contract drift: DTOs on both sides with no single source of truth.

**Real-world echo:** This is the internal-call smell before Tadka Day 8 extracts Payment — except our Day 8 wound was temporal coupling + fault isolation, not payload shape. For *internal* high-frequency reads (menu, rider location, inventory), gRPC/protobuf is the usual fix.

## 3. Exact Steps to Induce & Observe the Break
**Prerequisites:** Node.js v18+.

**On Windows (PowerShell):**
```powershell
cd tadka\toydemo\day-03-api-primitives\grpc-vs-rest-toy
node index.js --mode=break
```

**On macOS/Linux:**
```bash
cd toydemo/day-03-api-primitives/grpc-vs-rest-toy
node index.js --mode=break
```

**Observe:**
- `Round trips: 200`
- `Wire bytes:` ~300–400 KB (fat JSON × 200)
- `Simulated time:` ~400ms+ (200 × 2ms RTT model)

**Strongly recommended — real HTTP + gRPC servers:**
```powershell
npm install
node real-bench.js --mode=break
```

**Observe on real bench:**
- `Round trips: 200`
- `Wall time:` typically 100–800ms depending on machine (sequential localhost HTTP still stacks)
- Compare to fix mode — the gap is the lesson.

## 4. The Fix
Replace chatty REST with one gRPC unary batch:

```protobuf
rpc GetMenuItemsBatch (GetMenuItemsBatchRequest) returns (GetMenuItemsBatchResponse);
```

Client sends `{ ids: [1,2,...,200] }` once. Server returns all items in one protobuf-encoded response.

**Apply the fix (simulation):**
```bash
node index.js --mode=fix
```

**Apply the fix (real servers):**
```bash
node real-bench.js --mode=fix
```

## 5. Steps to Verify the Fix
Re-run the identical 200-item workload.

| Metric | break (REST chatty) | fix (gRPC batch) |
|--------|---------------------|------------------|
| Round trips | 200 | 1 |
| Simulated time (index.js) | ~400ms | ~2ms |
| Real wall time (real-bench.js) | high (100ms+) | low (typically <20ms localhost) |
| Wire efficiency | JSON keys repeated 200× | Protobuf compact encoding |

The **round-trip count** and **wall time ratio** are the headline numbers. Byte counts in `real-bench.js` use a JSON proxy for the gRPC response size in the report; protobuf on the wire is smaller — the simulation's `protobufEstimateBytes` shows the intended wire-shape gap.

## 6. Full Run Instructions
**Zero-dependency smoke (do this first):**
```powershell
cd tadka\toydemo\day-03-api-primitives\grpc-vs-rest-toy
node index.js --mode=break
node index.js --mode=fix
```

**Full realistic run (real HTTP + gRPC — no Docker required):**
```powershell
cd tadka\toydemo\day-03-api-primitives\grpc-vs-rest-toy
npm install
node real-bench.js --mode=break
node real-bench.js --mode=fix
```

**Optional — larger workload:**
```powershell
$env:ITEM_COUNT=500
node real-bench.js --mode=break
node real-bench.js --mode=fix
```
At 500 items, REST chatty becomes painfully slow; gRPC stays one round trip.

**Clean up:** No persistent state. Servers exit when the script finishes. `node_modules/` is gitignored per toy convention.

**Versions used:**
- Node: v18+
- @grpc/grpc-js: ^1.12
- No Docker required for this toy

## 7. Test Cases & Expected Results
| Test | Command | Broken Result | Fixed Result | Notes |
|------|---------|---------------|--------------|-------|
| Test 1: Simulation break | `node index.js --mode=break` | 200 round trips, ~400ms sim time | — | Instant, zero deps |
| Test 2: Simulation fix | `node index.js --mode=fix` | — | 1 round trip, ~2ms sim time | Compare bytes/item |
| Test 3: Real REST chatty | `node real-bench.js --mode=break` | 200 round trips, wall time 100ms+ | — | Requires `npm install` |
| Test 4: Real gRPC batch | `node real-bench.js --mode=fix` | — | 1 round trip, wall time <20ms typical | Same 200 items |
| Test 5: Scale stress | `ITEM_COUNT=500 node real-bench.js --mode=break` | Wall time >> fix mode | `ITEM_COUNT=500 node real-bench.js --mode=fix` still ~1 RTT | Optional |

## 8. Troubleshooting
- **`Cannot find module '@grpc/grpc-js'`** — Run `npm install` in this directory first.
- **Port in use (31081/31082)** — Set `$env:REST_PORT=31091; $env:GRPC_PORT=31092` then re-run.
- **Real bench slower than simulation** — Expected on first run (JIT, grpc startup). Shape (200 vs 1 RTT) matters more than absolute ms.
- **ECONNREFUSED** — Script starts servers automatically; if you modified the script, ensure `startRestServer` / `startGrpcServer` complete before the client runs.
- **"Works on my machine" latency** — Localhost RTT is sub-ms; in a real K8s cluster internal RTT is 1–5ms — multiply by 200 for REST chatty and the pain scales.

## 9. Cross-Stack Notes
- **Node.js (this toy):** `@grpc/grpc-js` + `proto-loader`. REST uses built-in `http`.
- **.NET / Tadka:** `Grpc.Net.Client` + `.proto` codegen; REST would be `HttpClient` in a loop. Tadka Day 8 uses sync HTTP to Payment — a deliberate teaching step before Kafka; gRPC is the alternative for *sync internal reads* when you need batch efficiency.
- **Java/Spring:** gRPC spring-boot-starter + `ManagedChannel`; REST = `RestTemplate`/`WebClient` loop (the anti-pattern this toy shows).
- **Go:** `grpc.Dial` + generated stub; REST = `http.Get` loop.
- **When REST is still right:** Public browser APIs, CDN-cacheable resources, debuggability with curl, third-party integrations. gRPC shines *inside* the mesh.

## 10. Curriculum Links
- Day 3 plan.md — API style selection beat.
- Day 3 option-space.md — REST vs gRPC trade-off table.
- Day 8 ADR-025 — sync HTTP bridge (contrast: we chose HTTP for teaching temporal coupling; gRPC would reduce bytes/RTTs on that hop).
- Suggested instructor note: "Run break+fix before the api-style-selection segment so students have numbers before the option-space slide."

## 11. Failure-First Narrative (for slides/instructor)
"Ordering needs 200 menu prices to place one order. The junior engineer wrote a loop: 200 internal HTTP GETs. Each one is 'fast' — 2 milliseconds — but 200 × 2ms is 400ms *before* JSON parsing, *before* connection pool contention, *before* you add retries. And every response carries full JSON field names for every dish. The fix is not 'make REST faster' — it's change the contract: one gRPC batch call, protobuf on the wire, generated client and server from the same `.proto`. One round trip. Cost stays flat as long as the batch fits one RPC. That's why internal APIs are not the same design problem as your public REST surface."

## 12. Limitations & What This Toy Does Not Cover
- Does not implement HTTP/2 REST (REST batch endpoint) — a valid middle ground; gRPC still wins on codegen + protobuf efficiency for *service-to-service* contracts.
- Does not cover gRPC streaming (server streaming menu updates) — unary batch is enough for the pricing hot path.
- Does not cover TLS/mTLS, service mesh, or grpc-gateway (REST facade in front of gRPC).
- Does not cover GraphQL batching — different trade-off (flexible client queries vs strong server contract).
- Localhost benchmark understates WAN RTT; the round-trip *count* argument survives any network.

**How to contribute updates:**
- Capture your machine's real-bench numbers in this doc.
- Add a REST batch endpoint as an optional "middle" mode if instructors want a three-way compare.
- Update the living plan when user-verified.

---

*Recommended teaching flow:*
1. `node index.js --mode=break` then `--mode=fix` live (30 seconds, zero setup).
2. `npm install` once before class; `node real-bench.js` break then fix.
3. Show `proto/menu.proto` — "this file IS the contract between Ordering and Restaurant."
4. Option-space slide: public REST at the gateway (Day 11 YARP), gRPC inside the mesh.