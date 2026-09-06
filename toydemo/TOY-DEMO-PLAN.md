# TOY-DEMO IMPLEMENTATION PLAN (Failure-First, Day-Wise, Polyglot)
**Project:** D:\work\desi-architect (Tadka + cohort-prep)
**Goal:** Create isolated, failure-first toy demos for every major system design topic/concept that has conceptual coverage in the curriculum (domain-primers, interview-track, coverage matrix, teardowns, option-spaces) but lacks a runnable "show what breaks then how to fix" implementation inside the core Tadka app.
**Style:** Exact match to existing Tadka pedagogy (sharding-demo + break-kits + DEMOS.md): first demonstrate the realistic failure (with numbers/metrics), then the fix, re-measure, document trade-offs + revisit triggers.
**Languages & Runtimes:** Mix of docker (for multi-container sims), Node.js (easy high-concurrency WS, workers, servers), JavaScript (client/load gens), .NET (alignment with Tadka for some demos).
**Structure:** `tadka/toydemo/` folder. Day-wise organization. Day-wise feature branches for development (e.g. `toydemo-day-06-realtime`).
**Living Plan:** This plan document will be saved first at `D:\work\desi-architect\toydemo\TOY-DEMO-PLAN.md` (and kept updated with progress checkboxes, dates, and links to per-demo deep docs). Progress of each phase/demo will be marked here after completion. After each specific demo + its deep "how to run and test" document is finished, stop and ask the user to personally test it (following the deep doc) and give explicit confirmation before any commit or moving to the next phase/demo.

**Core Constraints from User**
- Failure first: always show "what will break in this scenario" before the fix.
- Day wise in toydemo folder + daywise branches.
- When introducing a topic in the cohort, show a runnable demo using docker/node/js/.net.
- Each demo must have a **deep document** on exactly how to run and test it.
- After a demo + its documentation is complete: ask user to test + wait for explicit confirmation before committing those changes.
- Update this plan (in the project copy) with progress after each phase.

## Phase 0 ΓÇö Foundation & Living Plan (Setup Only ΓÇö No Demos Yet)
**Objective:** Create the folder skeleton, master plan document in the project, and the reusable template for every toy's deep run/test doc. Establish branch and update process.

**Deliverables**
- `tadka/toydemo/` directory created.
- `D:\work\desi-architect\toydemo\TOY-DEMO-PLAN.md` = this living plan (copy of the session plan + progress tracking). It will be the single source of truth for status.
- `tadka/toydemo/TEMPLATE-TOY-RUN-AND-TEST.md` ΓÇö deep, reusable template that every individual toy must follow (see detailed outline below).
- `tadka/toydemo/README.md` (high-level index + "how these toys work with the cohort").
- .gitignore updates if needed (e.g. bin/, obj/, node_modules/ per toy).
- Git branch strategy documented: develop each logical group on `toydemo-day-XX-<slug>` branches; merge to main only after user confirmation per phase.

**Deep Document Requirement (Template Outline ΓÇö must be produced for every toy)**
Every toy gets its own `toydemo/<day-slug>/<topic>-toy/RUN-AND-TEST.md` that is comprehensive:
1. Overview & Why This Toy Exists (link to curriculum day, interview problem, domain-primer, or Gemini gap it closes).
2. The Failure Scenario (real-world system design question it simulates; what breaks without the pattern; expected bad symptoms).
3. Exact Steps to Induce & Observe the Break (copy-paste commands for Windows PowerShell, macOS, Linux; docker-compose up; load generators; what numbers/metrics to watch ΓÇö p99, errors, memory, QPS, correctness, etc.).
4. The Fix (step-by-step: env var, code change, config, or separate "fixed" entrypoint/script).
5. Steps to Verify the Fix (re-run the same load; compare before/after numbers; what the "good" output looks like).
6. Full Run Instructions (one-command smoke, full demo, clean up; prerequisites: Docker Desktop, Node, .NET SDK versions; any ports).
7. Test Cases & Expected Results (table: "Test 1: naive stampede ΓåÆ p99 > 8s and pool exhaustion; Test 2: after single-flight ΓåÆ p99 < 500ms and only 1 recompute").
8. Troubleshooting (common "works on my machine", docker volume issues, Redis not reachable, etc.).
9. Cross-Stack Notes (how the same pattern looks in Java/Spring, Go, plain Node, etc.).
10. Curriculum Links (which day plan / option-space / interview-pack problem / ADR this supports; suggested "run this toy before the live session" note).
11. Failure-First Narrative (short paragraph the instructor can read aloud or put on slide: "In a real system you would see X break like this...").
12. Limitations & What This Toy Does Not Cover (keeps scope honest).

**Progress Tracking in Living Plan**
After Phase 0 complete: mark `[x] Phase 0 done ΓÇö <date> ΓÇö user confirmed skeleton + template`.

**Verification for Phase 0 (no user test of demos yet)**
- Folder exists, plan document at D:\work\desi-architect\toydemo\TOY-DEMO-PLAN.md is up to date.
- Template file exists and is comprehensive.
- `git status` clean except new files.
- README index lists all planned toys with day mapping.

**Exit Criteria Before Moving On**
User reviews the skeleton (via this plan) and confirms "proceed to Phase 1".

## Phase 1 ΓÇö Day 03/04 API & Contract Primitives Toys
**Toys in this phase (mapped to when curriculum introduces contracts/pagination/API styles)**
1. Cursor Pagination Toy (failure: OFFSET death on large "feed"/order history at page 10k+; fix: keyset/cursor pagination with proper ordering + tie-breaker).
2. gRPC vs REST Internal Toy (failure: chatty REST payload bloat + latency for internal calls; fix: gRPC + protobuf streaming/unary).

**Per-Toy Deliverables**
- `toydemo/day-03-api-primitives/cursor-pagination-toy/`
- `toydemo/day-03-api-primitives/grpc-vs-rest-toy/`
- For each: full implementation (small .NET or Node + Docker if needed), + the deep `RUN-AND-TEST.md` following the Phase 0 template exactly.

**Implementation Steps for Each Toy in Phase**
a. Create the dir + basic structure (following sharding-demo as template for README style + failure demos 1..N).
b. Implement break path + load script that produces clear bad numbers.
c. Implement fix path.
d. Write the complete deep RUN-AND-TEST.md (use the template; fill with real commands, expected outputs, screenshots if useful).
e. Update this living plan (add `[x]` for this toy + link to its RUN-AND-TEST.md + date).
f. **STOP** ΓÇö do not commit. Ask the user: "Please test the <toy-name> demo by following its RUN-AND-TEST.md exactly. Report results (did the numbers match? any issues?). Confirm with 'yes, good to commit this toy' or feedback before I proceed or git commit."

**Progress Checkpoints (update in TOY-DEMO-PLAN.md)**
- [x] Cursor pagination toy + deep doc complete — 2026-06-17 — [RUN-AND-TEST.md](day-03-api-primitives/cursor-pagination-toy/RUN-AND-TEST.md) — user verified real-db.js (OFFSET ~14ms/80k rows vs cursor ~0.15ms/20 rows)
- [x] gRPC toy + deep doc complete — 2026-06-17 — [RUN-AND-TEST.md](day-03-api-primitives/grpc-vs-rest-toy/RUN-AND-TEST.md) — user verified real-bench.js (REST 200 RTT/88ms vs gRPC 1 RTT/27ms)
- After user confirmation for the whole phase: mark Phase 1 done and create the day-wise branch merge if desired.

**Verification for Phase (after user test/confirm)**
- Each toy runs cleanly on the user's machine following the deep doc.
- Before/after numbers are visible and match the doc.
- The deep doc is actually "deep" (copy-paste ready, covers Windows + docker).
- References can be added later in cohort-prep (not in this phase).

## Phase 2 ΓÇö Day 06 Cache, Realtime & Hot-Key Toys
**Toys**
1. Advanced Rate Limiter Algorithms Toy (multiple algorithms + distributed failure modes + hotspot).
2. Hot Key / Thundering Herd + Stampede Lock Toy (builds directly on Tadka Day 6 Redis work; show melt without single-flight, then fix).
3. Stateful WebSocket / Realtime Toy (beyond Tadka's SSE; presence, ordering, backplane, connection limits under scale ΓÇö perfect for WhatsApp/Chat drill).

**Same per-toy process as Phase 1** (create, implement failure-first, deep RUN-AND-TEST.md, update living plan, **STOP and ask for user test + explicit confirmation** before commit or next toy).

**Progress Checkpoints (update in TOY-DEMO-PLAN.md)**
- [x] Rate limiter algos toy + deep doc — 2026-06-17 — [RUN-AND-TEST.md](day-06-cache-realtime/rate-limiter-toy/RUN-AND-TEST.md) — committed on day-06
- [x] Hot key / stampede toy + deep doc — 2026-06-17 — [RUN-AND-TEST.md](day-06-cache-realtime/hot-key-stampede-toy/RUN-AND-TEST.md) — committed on day-06 (200 DB queries → 1)
- [x] Stateful WebSocket toy + deep doc — 2026-06-17 — [RUN-AND-TEST.md](day-06-cache-realtime/stateful-websocket-toy/RUN-AND-TEST.md) — committed on day-06 (break ~49% / fix 100% delivery)
- [x] Phase 2 fully user-confirmed and committed (2026-06-17).

## Phase 3 ΓÇö Day 09 Kafka/Async/Fan-Out & Stream Toys
**Toys**
1. Notification / Promotional Fan-Out Toy (transactional vs mass fan-out, DLQ, idempotency under redelivery).
2. Simple Stream Processing / Heavy Hitters / Sliding Window Toy (Kafka + consumer for counts or top-K, e.g. ad clicks or leaderboard; failure without proper state/windowing).

**Process identical to previous phases + strict user confirmation gate per toy.**

**Progress Checkpoints**
- [ ] Notification fan-out toy + deep doc
- [ ] Stream processing toy + deep doc
- Phase 3 confirmed.

## Phase 4 ΓÇö Day 15 Breadth / Teardown Toys (the 5 Domain Primers + Fan-Out)
**Toys (the ones explicitly called "Tadka doesn't build")**
1. Search / Inverted Index Toy (vs B-tree/LIKE scan failure on large corpus; build postings list; ranking; optional shard sim).
2. Video Streaming / HLS + CDN Toy (transcode pipeline sim, segmenter, manifest, edge cache containers, adaptive client).
3. OLAP / CDC / Analytics Toy (row-oriented aggregate pain vs columnar after CDC stream).
4. Object Storage (S3-like) Toy (pre-signed URLs, multipart, content-hash dedup vs storing blobs in "DB").
5. Web Crawler Toy (frontier + politeness rate-limit failure (blocked/throttled), bloom filter dedup vs naive set, spider trap detection).

**Bonus/Stretch if time in phase:** Feed fan-out push vs pull toy, simple CRDT merge toy for conflict resolution.

**Same rigorous process:** deep docs, living plan updates, stop after each toy's doc for user test + confirmation.

**Progress Checkpoints**
- [ ] Search toy + doc
- [ ] Video/HLS toy + doc
- [ ] OLAP/CDC toy + doc
- [ ] Object storage toy + doc
- [ ] Web crawler toy + doc
- (stretch items)
- Phase 4 fully confirmed.

## Phase 5 ΓÇö Integration, Polish & Curriculum Wiring
**After all core toys + docs are user-confirmed and committed in prior phases.**
- Enhance `tadka/toydemo/README.md` and main `tadka/README.md`.
- Add entries to `cohort-prep/DEMOS.md`.
- Update relevant day plans, option-space.md, interview-pack primers/track, SYSTEM_DESIGN_COVERAGE.md with "Toy demo: see toydemo/..." links and "run before class" notes.
- Update sharding-demo if it benefits from new consistency.
- Add any top-level runner or verification script.
- Final branch clean-up / tagging for day-wise.

**No new toys in this phase** ΓÇö only wiring + docs.

**User confirmation gate** for the integration changes before final commit.

## Cross-Cutting Rules (Apply to All Phases)
- **Living Plan Updates:** After every phase or individual toy completion, edit `D:\work\desi-architect\toydemo\TOY-DEMO-PLAN.md` (the project copy) with:
  - `[x] <toy or phase> completed ΓÇö <date>`
  - Link to the deep RUN-AND-TEST.md
  - Any user feedback notes or open issues.
- **Deep Per-Demo Documents:** Every toy must ship with a high-quality RUN-AND-TEST.md that a student or instructor can follow blind and get the failure ΓåÆ fix experience. Use the Phase 0 template.
- **User Test Gate (Mandatory):** After the code for a specific demo + its deep documentation is finished (and plan updated), **do not commit the changes yet**. Message the user with:
  "The <specific toy name> demo + its RUN-AND-TEST.md is ready in the branch. Please run it yourself by strictly following the deep document and report back the results (did the break/fix numbers appear as expected? Any friction?). Confirm with 'approved to commit <toy>' or give feedback before I commit or start the next item."
  Only after explicit confirmation: commit that isolated change (or the phase).
- **Failure-First + Numbers:** Every demo must produce clear, capturable before/after metrics (like the sharding demo's 80% vs 24.5%).
- **Reusability:** Follow sharding-demo structure (standalone, deterministic where possible, excellent README, "how this maps to Tadka + interview" section).
- **Scope Control:** If a toy grows too large, split or simulate (fake transcode, in-memory "ES", etc.). Prefer zero external paid services.
- **Branch Hygiene:** Use day-wise branches as primary development vehicle.

## Full Prioritized Toy List (Day-Mapped)
**Day 03/04:** Cursor pagination, gRPC vs REST
**Day 06:** Advanced rate limiter algos, Hot key/thundering herd, Stateful WebSocket (Chat archetype)
**Day 09:** Notification fan-out, Simple stream processing/heavy hitters
**Day 15 (breadth):** Search inverted index, Video HLS+CDN, OLAP+CDC, Object storage/S3, Web crawler (+ feed fan-out & CRDT as stretch)

This covers essentially every topic flagged in domain-primers.md, interview-track unsolved/solved, and prior Gemini-style reviews that lacked runnable failure-first code.

## Verification (Overall)
- Every toy has a deep RUN-AND-TEST.md that a third party can follow end-to-end on Windows + Docker.
- User has personally executed and confirmed each toy before it is committed.
- Curriculum materials (day plans, DEMOS, primers) point to the toys.
- Running a toy produces the exact "what breaks" behavior an interviewer would expect the student to diagnose.
- No pollution of main Tadka build/tests.

## Next Steps After Plan Approval
When user approves this plan:
1. Save / sync the latest version of this plan to `D:\work\desi-architect\toydemo\TOY-DEMO-PLAN.md`.
2. Begin **Phase 0** (skeleton only).
3. After Phase 0 skeleton + template is done, mark progress in the project plan, show user the files, get go-ahead for Phase 1.
4. For every subsequent toy: build + deep doc ΓåÆ update living plan ΓåÆ ask user to test using the deep doc ΓåÆ wait for explicit "approved to commit" before git commit.

This structure guarantees the user stays in the loop for testing and commits, exactly as requested.

---

**Current Progress (update this section after every completion)**
- [x] Full phase-wise plan created, revised, and approved by user.
- [x] User: "approved start with phase 0" (2026-06-17) - Phase 0 **STARTED** in planning.
- [x] Phase 0 items:
  - [x] Directory tadka/toydemo/ created.
  - [x] Living TOY-DEMO-PLAN.md written here (this file).
  - [x] TEMPLATE-TOY-RUN-AND-TEST.md created with 12-section template.
  - [x] toydemo/README.md created with overview and mapping.
  - [x] tadka/README.md updated (Project Structure + short description paragraph).
- [x] Git: Phase 0 foundation committed **directly on the official day-01 branch** (commit 4c37d95). No separate toydemo-* side branches created. See new "Git Branching Strategy (clarified by user)" section below.
- [x] 2026-06-17: Cherry-picked foundation to day-03 (target branch for Day 03/04 concepts).
- [x] Phase 1 started on day-03: cursor-pagination-toy (index.js simulation + real-db.js Postgres verification + RUN-AND-TEST.md). real-db.js stdin fix for Windows multi-line SQL.
- [x] User tested cursor-pagination-toy (2026-06-17): `real-db.js --mode=break` (~14ms, 80,020 rows, 729 buffer hits) vs `--mode=fix` (~0.15ms, 20 rows, 6 buffer hits). Committed on day-03; carried to day-04 for gRPC toy.
- [x] Phase 0 complete and marked.
- [x] Phase 1 gRPC toy committed on day-04 (2026-06-17): user verified REST 200 RTT/88ms vs gRPC 1 RTT/27ms.
- [x] Phase 1 complete and marked (2026-06-17). Carried to day-06 for Phase 2.
- [x] Phase 2 rate-limiter-toy committed on day-06 (2026-06-17): fixed-window 150 allowed vs token-bucket 110/40.
- [x] Phase 2 hot-key-stampede-toy committed on day-06 (2026-06-17): break 200 DB queries / fix 1.
- [x] Phase 2 complete on day-06 (2026-06-17). Phase 3 started on day-09.
- [ ] Phase 2 complete (hot-key + WebSocket toys pending).

*End of living plan. Follow phases and gates strictly. User gate required before any commit of changes.*

**Phase 0 Execution Log**
- 2026-06-17: Skeleton created on day-16 working tree ΓåÆ stashed ΓåÆ cherry-picked directly onto official day-01 (commit 4c37d95) ΓåÆ side branch deleted. Now lives on day-01 as requested.
- Plan updated on day-01 with the clarified strategy.

## Git Branching Strategy (clarified by user)
**User clarification**: "we have day-01, day-02 already for each day.. i meant to commit in the same days branch and not creating new branches"

- All toydemo/ work (foundation + each toy) is committed **directly on the existing official day-NN branches** (day-01, day-02, day-03, day-06, day-09, day-15, etc.). No new `toydemo-*-*` side branches.
- The Phase 0 foundation skeleton (this plan, the template, the common README) was introduced on **day-01** (earliest relevant branch) via direct commit (4c37d95).
- For a toy whose concept "belongs" to a specific day (e.g. WebSocket/stateful realtime + hot key toys belong with Day 06 curriculum):
  - `git checkout day-06`
  - `git cherry-pick 4c37d95`  (or the latest toydemo commit from previous day) ΓÇö this carries/moves the previous toydemo/ state onto day-06.
  - Add the day-specific toy sub-folder + its deep RUN-AND-TEST.md under `toydemo/day-06-.../`
  - `git add toydemo/ ; git commit ...` directly on day-06.
- To advance to the next day (e.g. when later adding Day 09 toys): `git checkout day-09; git cherry-pick <the day-06 toydemo commit hash>` ΓÇö this moves the entire toydemo/ history (foundation + Day 06 toys) forward.
- This keeps `toydemo/` versioned together with the Tadka code state for that teaching day, exactly like the rest of the repo. The day-NN branches are the single source of truth for both the app and its supporting teaching toys.

**How to Carry Changes Forward (example)**
```bash
# When starting work for Day 06 toys
git checkout day-06
git cherry-pick 4c37d95   # brings the foundation from day-01
# add Day 06 toys...
git add toydemo/
git commit -m "toydemo: add Day 06 toys (rate-limiter, hot-key, WebSocket) with failure-first demos and deep RUN-AND-TEST.md"

# Later, when doing Day 09
git checkout day-09
git cherry-pick <day-06-toydemo-commit>  # moves previous toydemo state forward
```

The living plan travels with the toydemo/ commits.

Current state: Phase 0 foundation is cleanly on official day-01 (no extra branches). Ready to be carried when we implement toys on their respective days.
- 2026-06-17: Directory created, living plan, template, toydemo/README, tadka/README update done.
- Next: User review of skeleton + explicit confirmation before Phase 1.

---

## Phase 0 Execution Details (Concrete Steps - to be performed when mode allows)
1. Create directory: mkdir -p D:\work\desi-architect\tadka\toydemo
2. Write the full current plan content to D:\work\desi-architect\tadka\toydemo\TOY-DEMO-PLAN.md (use write tool; this becomes the living doc for progress).
3. Create D:\work\desi-architect\tadka\toydemo\TEMPLATE-TOY-RUN-AND-TEST.md with the full 12-section outline filled as template (copy from Phase 0 description).
4. Create D:\work\desi-architect\tadka\toydemo\README.md with:
   - Overview of purpose (failure-first toys for missing topics to support curriculum breadth).
   - List of planned toys with day mapping.
   - How to run any toy (refer to individual deep docs).
   - Contribution: always add deep doc, update this living plan.
5. Update D:\work\desi-architect\tadka\README.md : Add a new section after "k6/" in Project Structure, e.g.:
   ```
   Γö£ΓöÇΓöÇ toydemo/                 # Failure-first toy demos for breadth topics (not in main Tadka evolution)
   Γöé   ΓööΓöÇΓöÇ day-XX-*/            # Day-wise toys with RUN-AND-TEST.md
   ```
   And a short para: "See toydemo/README.md for standalone demos used to teach topics not implemented in the core Tadka app (e.g. search, WebSockets, OLAP). Each includes failure-first demos using docker/node/.net."
6. (Optional) Create .gitkeep in subdirs if needed.
7. Create initial git branch if developing: git checkout -b toydemo-phase-0-foundation
8. Mark in living plan: [x] Phase 0 complete ΓÇö <date> (after user review of created files).
9. **User gate**: Show user the created skeleton + template. Ask for confirmation "Phase 0 skeleton looks good, proceed to Phase 1" before any further work or commits.

**Verification for Phase 0 (read-only checks + user visual review)**:
- toydemo/ folder exists in tadka/.
- TOY-DEMO-PLAN.md exists and matches this (with progress updated).
- TEMPLATE file has the 12 sections.
- toydemo/README.md has index and links.
- tadka/README.md has the addition (no breakage to existing content).
- No other files modified.

Once Phase 0 done per user confirm, update progress in the project TOY-DEMO-PLAN.md and prepare for Phase 1.

All prior exploration (list_dir, read_file, grep on tadka/ and prep materials) informed the structure to reuse sharding-demo pattern, match existing README styles, and align with curriculum days. No non-readonly actions taken outside plan file edits.
