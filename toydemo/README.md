# Tadka Toy Demos (toydemo/)

**Purpose:** Standalone, failure-first runnable demos for system design topics that have conceptual or interview coverage in the Desi Architect curriculum (via domain-primers, interview-track, SYSTEM_DESIGN_COVERAGE, teardowns, option-spaces, and prior reviews) but are **not implemented** in the core Tadka monolith-to-microservices evolution.

Tadka focuses on OLTP food-delivery patterns (monolith, DB scaling, Redis cache, modular monolith, service extraction with HTTP then Kafka/Outbox/Saga, polyglot geo, gateway, observability, resilience, load testing, cost).

These toys fill the "breadth" gaps so students can **see and break** the patterns before/while learning them conceptually, matching the cohort's "earned, not assumed" and "show it, don't just tell it" philosophy (see DEMOS.md and CLAUDE.md).

## How These Toys Work
- **Failure first:** Every toy has a "break" mode that demonstrates the realistic failure an architect or interviewer would encounter (with measurable numbers: latency, memory, errors, correctness, throughput).
- **Then the fix:** Switch to the "fixed" version (pattern from curriculum or primer), re-run, compare before/after, document trade-offs and "revisit when".
- **Day-wise:** Organized under day- folders corresponding to when the related pattern or breadth topic is introduced in the cohort (from interview-track and day plans).
- **Polyglot & easy to run:** Mix of Docker (for services/multi-node sims), Node.js (high-concurrency WS, workers, simple servers), .NET (for consistency with Tadka where useful), JS (client sims/load gens). Most are self-contained like `samples/sharding-demo`.
- **Deep documentation:** Every toy ships with a `RUN-AND-TEST.md` that is copy-paste ready for Windows PowerShell + Docker + macOS/Linux. Follow it to reproduce the exact experience.

See the master living plan at `TOY-DEMO-PLAN.md` for full prioritized list, phase-wise implementation status, and progress.

## Planned / In-Progress Toys (Day-Mapped)
See `TOY-DEMO-PLAN.md` for the authoritative list and status. High-level:

- **Day 03/04 (API/contracts/pagination):** Cursor Pagination Toy, gRPC vs REST Internal Toy.
- **Day 06 (cache/realtime/hot keys):** Advanced Rate Limiter Algorithms Toy, Hot Key / Thundering Herd + Stampede Lock Toy, Stateful WebSocket / Chat Toy (for WhatsApp archetype).
- **Day 09 (Kafka/async/fan-out):** Notification / Promotional Fan-Out Toy, Simple Stream Processing / Heavy Hitters / Sliding Window Toy.
- **Day 15 (breadth/teardowns/primers):** Search / Inverted Index Toy, Video Streaming / HLS + CDN Toy, OLAP / CDC / Analytics Toy, Object Storage (S3-like) Toy, Web Crawler Toy (+ stretch Feed fan-out and CRDT toys).

Sharding already has a great runnable demo in `../samples/sharding-demo/` (enhance if needed for hot-shard under load).

Each toy folder will contain:
- Code / scripts / docker-compose for break + fixed.
- `RUN-AND-TEST.md` (the deep doc).
- Small README if needed.

## Running a Toy
1. cd into the specific toy dir (e.g. `cd day-06-cache-realtime/stateful-websocket-toy`).
2. Follow **that toy's `RUN-AND-TEST.md`** exactly (it has all commands, expected outputs, troubleshooting).
3. Run the break version first → observe the failure metrics.
4. Apply fix → re-run → compare.
5. Read the "Curriculum Links" section to connect back to the day's plan/option-space/interview problem.

**Common prerequisites (per toy doc):** Docker Desktop, Node 20+, .NET 10 SDK (for some), PowerShell/bash.

No changes to main Tadka build/tests (these are isolated, like sharding-demo).

## Contribution / Adding a New Toy
- Follow the template in `TEMPLATE-TOY-RUN-AND-TEST.md`.
- Always start with failure scenario + reproducible break demo + metrics.
- Update `TOY-DEMO-PLAN.md` with progress (checkbox + date + link to the RUN-AND-TEST.md).
- After your toy + doc is done, **stop and ask the maintainer/user to test it following the deep doc and confirm before committing the changes**.
- Add cross-references in the relevant `cohort-prep/day-XX/plan.md`, `option-space.md`, `DEMOS.md`, `interview-pack/*` as part of integration (Phase 5).

## Relation to Existing Materials
- Reuses the exact style and lessons from `../samples/sharding-demo/README.md` and `Program.cs` (failure demos, takeaways, "How this maps to Tadka", deterministic output).
- Complements `../docs/break-kits/`, `cohort-prep/DEMOS.md`, and day plans' "demo-first" approach.
- Supports the language-agnostic claim (toys in multiple stacks + cross-stack notes in the deep docs).
- Helps close gaps identified in prior reviews (Gemini-style audits, grokreview.md) for interview breadth.

## Current Status
See `TOY-DEMO-PLAN.md` (the living document) for the latest progress per phase and per toy.

**Phase 0 (Foundation) is in progress / just started per user approval:**
- Directory created.
- Living plan written here.
- Template created.
- This README created.
- tadka/README.md update planned (will be done in this phase).
- User confirmation gate at end of phase for the skeleton before moving to Phase 1 toys.

Run `ls` or explore the subdirs for current toys (initially the template and plan only; toys added in later phases).

---

**This folder and its demos exist to make the "missing" topics feel real and earned, the same way Tadka makes monolith-to-distributed feel earned.** 

Questions or issues with a toy? Follow the deep doc first, then open a discussion with the exact before/after numbers you saw.