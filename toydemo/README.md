# Tadka Toy Demos (toydemo/)

Failure-first, runnable demonstrations for major system design topics that are **discussed** in the Desi Architect cohort curriculum (day plans, option-space.md, domain primers, interview track) but are **not implemented** inside the core Tadka monolith-to-microservices evolution.

The goal is the same pedagogy as the existing `samples/sharding-demo/`: show what breaks with realistic numbers/metrics first, then apply the fix, re-measure, discuss trade-offs and revisit triggers. These are standalone teaching aids.

## Why these exist
Prior CTO-level and Gemini-style reviews of the cohort materials repeatedly flagged breadth gaps:
- Cursor vs offset pagination death at deep pages
- gRPC vs REST internal call costs
- Hot keys + thundering herd / stampede
- Stateful realtime / WebSocket backplanes
- Search / inverted index vs LIKE or full table scan
- Video (HLS + edge), OLAP/CDC, object storage, web crawler, fan-out, etc.

Tadka itself focuses on the "happy path" evolution of a food delivery platform (.NET, Postgres schema-per-domain, Redis, Kafka outbox, YARP, OTEL, k6, etc.). The toydemo/ toys fill the "show the painful alternative + the fix" experiences without polluting the main app.

## Structure
```
tadka/toydemo/
├── TOY-DEMO-PLAN.md           # Living plan (phases, day mapping, progress, git rules, user gates)
├── TEMPLATE-TOY-RUN-AND-TEST.md
├── README.md                  # This file
├── day-03-api-primitives/
│   ├── cursor-pagination-toy/ # real-db.js + optional index.js
│   └── grpc-vs-rest-toy/      # real-bench.js + optional index.js
├── day-06-cache-realtime/
│   ├── redis-cli-playground/  # index.html — Hour-1 command gym
│   ├── rate-limiter-toy/
│   ├── hot-key-stampede-toy/  # real-redis.js
│   └── stateful-websocket-toy/ # real-chat.js
├── day-09-kafka-async/
│   ├── notification-fanout-toy/ # real-kafka.js
│   └── stream-processing-toy/
└── day-15-breadth/
    ├── search-index-toy/      # real-db.js (GIN vs LIKE)
    ├── video-hls-cdn-toy/     # real-demo.js (HTTP origin/edge)
    ├── olap-cdc-toy/          # real-db.js + real-cdc.js
    ├── object-storage-toy/    # real-demo.js (bytea vs presigned)
    └── web-crawler-toy/       # real-crawl.js (politeness + dedup)
```

Each toy ships `RUN-AND-TEST.md` (the deep guide) and defaults `package.json` scripts to the **real** entrypoint.

## How any toy works (failure-first)
1. Run the "break" scenario (naive OFFSET, no single-flight, chatty REST, blobs in DB, etc.).
2. Observe clear bad metrics (rows examined, 429 throttles, origin egress MB, etc.).
3. Apply the fix (cursor/keyset, presigned URL, polite crawler, HLS+edge, etc.).
4. Re-run the same inducing workload.
5. Compare numbers + (when applicable) real EXPLAIN plans or HTTP stats.
6. Read the narrative in the toy's RUN-AND-TEST.md for the slide-friendly story.

**Real implementation first:** every toy has a runnable real path (Postgres, HTTP, Redis, Kafka, files). Pure-JS simulation is optional smoke only where it exists.

## Running a toy
See the individual `RUN-AND-TEST.md` inside each toy folder. Windows PowerShell commands included.

Typical real path:
```powershell
cd D:\work\desi-architect\tadka
docker compose up -d postgres   # when the toy needs Postgres

cd toydemo\day-15-breadth\search-index-toy
node real-db.js --mode=break
node real-db.js --mode=fix
```

## Planned toys — status (see TOY-DEMO-PLAN.md for gates)

| Phase | Day | Toys | Status |
|-------|-----|------|--------|
| 1 | 03/04 | cursor-pagination, grpc-vs-rest | done |
| 2 | 06 | rate-limiter, hot-key-stampede, stateful-websocket | done |
| 3 | 09 | notification-fanout, stream-processing | done |
| 4 | 15 | search-index, video-hls-cdn, olap-cdc, object-storage, web-crawler | done (day-15) |

Stretch: feed fan-out push vs pull (optional). **CRDT:** `day-15-breadth/crdt-counter-toy` (G-Counter merge).

## Relation to other demos in the repo
- `samples/sharding-demo/` — original reference for this style.
- Main Tadka evolution — the "build the right thing" path. Toys show "what happens if you build the common wrong thing first".

## Contribution rules (strict)
- Every toy must ship with a complete `RUN-AND-TEST.md` following the 12-section template.
- Failure-first + visible numbers/metrics required.
- After code + doc: **stop** — user runs the deep doc and gives explicit "approved to commit" before git commit.
- All work lands on official `day-NN` branches (cherry-pick forward; no separate toydemo-* branches).
- Update `TOY-DEMO-PLAN.md` after every toy and phase.

## Curriculum wiring (Phase 5 — done)
- [`cohort-prep/DEMOS.md`](../../desiarchitect-website/cohort-prep/DEMOS.md#toy-demos--breadth-topics-tadka-doesnt-build--tadkatoydemo) — full toy index with break/fix numbers
- Day run-sheets: `cohort-prep/day-03`, `day-06`, `day-09`, `day-15` — pre-class toy callouts
- [`domain-primers.md`](../../desiarchitect-website/cohort-prep/interview-pack/domain-primers.md) — one runnable toy per breadth primer
- [`SYSTEM_DESIGN_COVERAGE.md`](../../desiarchitect-website/cohort-prep/SYSTEM_DESIGN_COVERAGE.md) — toy links on drill/optional rows
- **Inventory:** `toydemo/verify-all.ps1` (list all toys; `-Run break|fix` for smoke)

---

Maintained as part of the Desi Architect teaching stack. `TOY-DEMO-PLAN.md` is the current source of truth for status and process.