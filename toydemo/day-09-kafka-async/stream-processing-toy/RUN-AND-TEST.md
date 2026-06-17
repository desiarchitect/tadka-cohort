# RUN-AND-TEST.md for Stream Processing / Heavy Hitters Toy

**Toy:** Stream Processing / Heavy Hitters Toy
**Day Introduced:** Day 09 (Kafka consumers, windowed aggregation)
**Related Curriculum:** Day 9 async backbone, interview leaderboard/ad-click problems, hot-cell surge (Day 14/15).
**Purpose:** Failure-first demo of unbounded click storage + all-time leaderboard (wrong leader, OOM risk) vs tumbling-window buckets (bounded state, correct last-5-min top-K).

## 1. Overview & Why This Toy Exists
Kafka gives you the stream — but consumers still need **windowing** for "top restaurants in the last 5 minutes." The naive approach stores every click and recounts, or reports all-time leaders when the product asked for a **sliding window**.

## 2. The Failure Scenario
**Workload:** 100k click events over 10 simulated minutes. Minutes 0–5: old CornerHouse promo dominates. Minutes 6–9: cricket wicket → Meghana surge.

**Naive pattern:** Append every event to an array; leaderboard = count all events ever.

**Bad symptoms:**
- State size = event count (100k → millions in prod → OOM)
- Recount CPU grows linearly
- **Wrong answer:** all-time leader is CornerHouse; correct last-5-min leader is Meghana

## 3. Exact Steps to Induce & Observe the Break
```powershell
cd tadka\toydemo\day-09-kafka-async\stream-processing-toy
node index.js --mode=break
```

**Observe:**
- `State size: 100,000 events (unbounded!)`
- `Reported top-3: CornerHouse:...` (all-time)
- `Leader correct: NO — stale/wrong window`

## 4. The Fix
Tumbling 1-minute buckets; prune buckets older than 5 minutes; aggregate only live buckets.

```powershell
node index.js --mode=fix
```

**Observe:**
- `State size: 6 minute buckets` (bounded)
- `Leader correct: YES` — Meghana wins last 5 min

## 5. Steps to Verify the Fix
| Metric | break | fix |
|--------|-------|-----|
| State size | 100,000 events | ~6 buckets |
| #1 restaurant | CornerHouse (stale) | Meghana (correct) |
| Leader correct | NO | YES |

## 6. Full Run Instructions
```powershell
node index.js --mode=break
node index.js --mode=fix
```

Zero dependencies. Optional: `$env:EVENTS=500000` for scale stress.

## 7. Test Cases & Expected Results
| Test | Command | Broken | Fixed |
|------|---------|--------|-------|
| Wrong window leader | `--mode=break` | CornerHouse #1 | — |
| Correct 5-min leader | `--mode=fix` | — | Meghana #1 |
| Bounded state | both | 100k entries | ≤6 buckets |

## 8. Troubleshooting
- **Leader differs run-to-run** — random surge uses `Math.random()`; shape holds (break wrong, fix right). Seed if you need determinism.
- **Slow with EVENTS=1M** — expected; demonstrates recount cost growth.

## 9. Cross-Stack Notes
- **Kafka Streams / Flink:** native windowed aggregation.
- **Redis:** sorted sets per minute + TTL, or `ZINCRBY` with key per time bucket.
- **Count-Min Sketch:** approximate heavy hitters at massive scale (stretch).

## 10. Curriculum Links
- Day 9 Kafka consumer lag = unbounded state symptom
- Interview-pack viral URL / leaderboard problems
- Day 15 surge-cell = hot key in time window

## 11. Failure-First Narrative
"The dashboard says CornerHouse is #1. Marketing just launched a Meghana surge — it's on fire *right now*. Your leaderboard counts every click since dawn. You're answering the wrong question with unbounded memory. Fix: tumbling minute buckets, drop anything older than five minutes, aggregate six buckets. State stays tiny; the leader matches reality."

## 12. Limitations
- In-memory simulation only (no Kafka consumer).
- Tumbling windows (not hopping/session windows).
- Exact counts (not Count-Min Sketch approximation).

---

*Teaching flow:* break shows wrong leader + 100k state → fix shows Meghana + 6 buckets → tie to Kafka consumer lag and OOM.