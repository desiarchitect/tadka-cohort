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
└── day-03-api-primitives/
    └── cursor-pagination-toy/
        ├── index.js           # Fast zero-dep simulation (break / fix modes)
        ├── real-db.js         # Real Postgres verification using project's tadka-postgres container + EXPLAIN (ANALYZE, BUFFERS)
        ├── package.json
        └── RUN-AND-TEST.md    # The deep, copy-paste-ready guide (12 sections)
```

Each toy is organized by the **curriculum day** when the concept is introduced.

## How any toy works (failure-first)
1. Run the "break" scenario (naive OFFSET, no single-flight, chatty REST, naive fan-out, etc.).
2. Observe clear bad metrics (rows examined growing to 80k, high p99, thundering herd, wasted IO, etc.).
3. Apply the fix (cursor/keyset, single-flight cache stampede lock, gRPC, proper fan-out with outbox/inbox, etc.).
4. Re-run the same inducing workload.
5. Compare numbers + (when applicable) real EXPLAIN plans or load test output.
6. Read the narrative in the toy's RUN-AND-TEST.md for the slide-friendly story.

Most toys provide:
- A zero-dependency / instant "smoke" path (pure JS arrays, no Docker) for live teaching.
- A "real verification" path that uses the project's existing docker-compose services (postgres, redis...) so students see production-like behavior (query planner, buffer stats, real timings) without new infrastructure.

## Running a toy
See the individual `RUN-AND-TEST.md` inside each toy folder. They are the single source of truth and follow the template exactly (Windows PowerShell commands included).

Typical quick smoke:
```powershell
cd tadka\toydemo\day-03-api-primitives\cursor-pagination-toy
node index.js --mode=break
node index.js --mode=fix
```

For the real DB truth (when the toy provides a real-*.js or raw SQL):
```bash
# from tadka/
docker compose up -d postgres
cd toydemo/day-03-api-primitives/cursor-pagination-toy
node real-db.js --mode=break
node real-db.js --mode=fix
```

## Relation to other demos in the repo
- `samples/sharding-demo/` — the original reference implementation of this style (failure numbers, maps-to-Tadka section, DEMOS.md). The toydemo/ toys follow the same spirit but live outside the main app so they can be more focused and polyglot.
- Main Tadka evolution (src/, k6/, docker-compose) — the "build the right thing" path. Toys show "what happens if you build the common wrong thing first".

## Planned toys (day-mapped, see TOY-DEMO-PLAN.md for status)
**Phase 1 (Day 03/04 - API & contract primitives)**
- cursor-pagination-toy (OFFSET death vs cursor/keyset + real EXPLAIN)
- gRPC vs REST internal toy (payload bloat + latency)

**Phase 2 (Day 06 - Cache, realtime, hot paths)**
- Rate limiter algorithms
- Hot key / thundering herd + stampede lock (builds on Tadka Redis day)
- Stateful WebSocket / realtime toy (presence, ordering, backplane limits)

**Phase 3 (Day 09 - Kafka / async / fan-out)**
- Notification / promotional fan-out (transactional vs mass, DLQ, idempotency)
- Simple stream processing / heavy hitters / sliding windows

**Phase 4 (Day 15 - Breadth / domain primers)**
- Search / inverted index toy
- Video streaming (HLS + CDN + adaptive client)
- OLAP / CDC / analytics row vs columnar
- Object storage (S3-like pre-signed, multipart, dedup)
- Web crawler (frontier, politeness, bloom, traps)
- (stretch) Feed fan-out push vs pull, basic CRDT merge

See the living `TOY-DEMO-PLAN.md` for exact progress checkboxes, user-confirmation gates, and branch strategy.

## Contribution rules (strict)
- Every toy must ship with a complete `RUN-AND-TEST.md` following the 12-section template.
- Failure-first + visible numbers/metrics required.
- After code + doc for a toy is done: **stop**. Do not commit. Ask the user (or reviewer) to personally run the deep document end-to-end and give explicit "approved to commit <toy name>" before any git add/commit.
- All work lands on the official `day-NN` branches (cherry-pick foundation forward; never create separate toydemo-* side branches).
- Update `TOY-DEMO-PLAN.md` (the project copy) after every toy and phase.

## Curriculum wiring (future phases)
Once toys are user-approved and landed on the day branches:
- Day plans get "run this toy before class" callouts.
- option-space.md and interview-pack get links.
- `cohort-prep/DEMOS.md` and main `tadka/README.md` get refreshed references.

This keeps the "language-agnostic 80/20 + failure analysis grading" claim honest with runnable artifacts.

## License / usage
Same as the rest of the cohort materials. Use for teaching the cohort and for your own interview/system design prep. Run the break path on candidates if you want to see whether they have felt the pain before.

---

Maintained as part of the Desi Architect teaching stack. The living plan (`TOY-DEMO-PLAN.md`) is the current source of truth for status and process.
