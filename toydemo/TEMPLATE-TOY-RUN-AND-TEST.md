# TEMPLATE: RUN-AND-TEST.md for a [Topic] Toy

**Toy:** [Short name, e.g. Cursor Pagination Toy]
**Day Introduced:** Day NN (topic area, e.g. API primitives / realtime / breadth)
**Related Curriculum:** Link to specific day plan, option-space.md, domain-primer, interview-pack archetype, or prior review gap this closes.
**Purpose:** One-sentence failure-first goal. What misconception does this make concrete?

## 1. Overview & Why This Toy Exists
[Explain the teaching problem. Why a runnable demo beats slides or "just tell them". Reference the exact curriculum spot and any reviews that flagged the missing hands-on piece.]

## 2. The Failure Scenario
Describe the realistic workload / user behavior that triggers the bad path.
- What the naive implementation does.
- Expected bad symptoms (latency, resource usage, correctness, UX, cost).
- Real-world examples (order history deep pagination, hot key stampede, fan-out thundering herd, etc.).

## 3. Exact Steps to Induce & Observe the Break
**Prerequisites:** (Node, Docker Desktop, .NET SDK, specific compose services, etc.)

**On Windows (PowerShell):**
```powershell
cd toydemo\day-NN-xxx\the-toy
node ... --mode=break
# or dotnet run ...
# or docker compose ...
```

**On macOS/Linux:**
```bash
cd toydemo/day-NN-xxx/the-toy
...
```

**Observe / numbers to watch:**
- List the key metrics printed or logged (p99, rows examined, error rate, memory, "rows removed by filter", etc.).
- What the bad path output should look like.

**Strongly recommended verification path (real infra when applicable):**
[If there is a simulation vs real split, call it out here exactly like the cursor toy. Direct the reader to run the real EXPLAIN / load / docker exec path.]

## 4. The Fix
Step-by-step what changes (code diff conceptually, config, new query pattern, single-flight wrapper, cursor instead of offset, etc.).
The "fixed" entrypoint or flag.

## 5. Steps to Verify the Fix
Re-run the identical inducing scenario with the fix applied / --mode=fix.
What the good numbers / plan / output must show.
Compare table or side-by-side expectations.

## 6. Full Run Instructions
**Zero-dependency smoke (recommended first):**
```bash
node ... --mode=break
node ... --mode=fix
```

**Full realistic run (with project's services / docker):**
1. `docker compose up -d` (or the relevant services: postgres, redis, kafka, etc.)
2. ...
3. Run the break then fix commands.
4. Cleanup commands.

**Versions / prerequisites matrix**
- Node / .NET / Docker versions used when this was captured.
- Postgres / Redis version from the Tadka compose.

## 7. Test Cases & Expected Results
| Test | Command / Scenario | Broken Result (numbers + symptoms) | Fixed Result | Notes |
|------|--------------------|------------------------------------|--------------|-------|
| Test 1: [name] | `--mode=break` | high cost / latency / errors | low constant / good p99 | ... |
| Test 2: ... | ... | ... | ... | ... |

Add as many as make the failure→fix obvious and reproducible.

## 8. Troubleshooting
- Common "works on my machine" issues (container name, port conflict, volume state, missing ANALYZE, node version).
- How to reset data (TRUNCATE, docker compose down -v, etc.).
- What the output means if the numbers are "close but not identical".
- Docker exec psql or k6 tips.

## 9. Cross-Stack Notes
How the same pattern / fix appears in:
- Node.js / raw SQL or Prisma/Sequelize
- .NET / EF Core / Dapper
- Java/Spring
- Go
- (others relevant to cohort)

Emphasize the universal idea, not framework magic.

## 10. Curriculum Links
- Exact day plan file + heading.
- option-space.md row if applicable.
- interview-pack problem / archetype.
- Suggested instructor note: "Before teaching X, have students run `node toydemo/... --mode=break` so they feel the pain."

## 11. Failure-First Narrative (for slides/instructor)
A short, speakable paragraph the teacher can read or put on a slide:
"In a real system you would see ... With the naive approach the database / cache / queue does X (examine 80k rows / thundering herd of 10k goroutines / ...). Latency / error rate / cost goes through the roof. The fix is Y. After the fix you see Z (constant cost, single in-flight computation, ...). This is the exact decision we make when designing ..."

## 12. Limitations & What This Toy Does Not Cover
- Honest scope (e.g. "this is a single-key hot path toy; does not simulate full cache stampede across many keys or network partitions").
- Things left as exercises (composite cursors, non-unique sort keys, multi-consumer stream processing, etc.).
- Production extras (authz on cursors, encryption of resume tokens, monitoring the bad metric in prod, etc.).

**How to contribute updates to this toy:**
- Capture fresh numbers / EXPLAIN / k6 output on current Tadka stack.
- Improve the simulation fidelity or add a new failure mode.
- Update this RUN-AND-TEST.md + the living TOY-DEMO-PLAN.md.
- Keep the failure-first structure.

---

*Recommended teaching flow:*
1. Run the fast/zero-dep path live so the class sees bad numbers instantly.
2. Then show the real infrastructure path (EXPLAIN, real load, docker service) or have students run it.
3. Discuss the "revisit trigger" (when do we accept the complexity of the fix?).

This combination makes the concept stick far better than diagrams alone.
