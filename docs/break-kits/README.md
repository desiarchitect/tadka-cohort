# Break Kits

A **Break Kit** is a scripted, reproducible failure for one week of the cohort. It turns a system-design concept from a slide into something students *watch break*, diagnose, and fix — then prove the fix with numbers.

Break Kits implement the per-week failure arc defined in [`../tadka-growth-story.md`](../tadka-growth-story.md). That document is the source of truth for *which* failure belongs to *which* week and *why*. This folder is *how* you run each one.

## The ritual (every kit, same shape)

```
Baseline → Break it → Read the symptoms → Enumerate the options
   → Decide (ADR) → Fix → Re-test → Compare
```

The deliverable each week is the **before/after measurement table + the ADR(s)** — never the code. The code is just evidence the decision worked.

## Contents

| Week | Kit | The failure | Status |
|------|-----|-------------|--------|
| 2 | [week-02.md](week-02.md) | Dinner-rush concurrency → seq scans → connection-pool exhaustion | ✅ reference template |
| 3 | week-03.md | Read load saturates the primary; replication lag breaks read-your-writes | ⏳ to do |
| 4 | week-04.md | Synchronous payment brownout stalls the whole monolith | ⏳ to do |
| 5 | week-05.md | Sync call-chain cascade; retries double-charge (no idempotency) | ⏳ to do |
| 6 | week-06.md | One change forces a full-platform redeploy (deploy blast radius) | ⏳ to do |
| 7 | week-07.md | Cascade failure you can't diagnose (no traces, no breaker) | ⏳ to do |
| 8 | week-08.md | Single-DB write ceiling; table outgrows RAM | ⏳ to do |

## Authoring a new kit

Copy [week-02.md](week-02.md) and keep its section order: **Scenario → Learning objectives → Prerequisites (incl. how to reproduce the baseline) → Step 0 induce → Step 1 baseline run → Step 2 diagnose → Step 3 options (cheapest-first table) → Step 4 decide/ADR → Step 5 fix → Step 6 re-test → Step 7 compare → Discussion/stretch → Instructor notes.**

Each kit needs three runnable pieces:

1. A **load or fault generator** (a k6 profile in `../../k6/`, and/or a fault-injection toggle).
2. A **way to induce the break on demand** (a SQL/script knob in `../../scripts/`, or a config flag) so it reproduces live in session.
3. A **clear "before/after" metric** so the fix is measured, not asserted.

Always walk the [scaling decision tree](../scaling-decision-tree.md) in Step 3 and **name the expensive options you reject** — rejecting sharding out loud is as much the lesson as choosing an index.
