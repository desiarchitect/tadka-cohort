# Day-branch evolution map (read this before you get confused)

Students check out `day-06` on Saturday and `day-12` the next weekend. **Those are not two random codebases** — each branch is a **snapshot of the same system after that day’s failures**. Features only appear on the day they are *earned*.

## How to use branches

| Goal | Branch |
|------|--------|
| Run the full platform (all demos) | `main` or `day-16` (same tip) |
| Rehearse a single day as taught | `day-NN` for that day |
| See “what changed since last weekend” | `git log day-06..day-12 --oneline` |

**Rule:** Prefer `main` for multi-day dry-runs. Use `day-NN` when the instructor says “we are on Day N only.”

## What appears when (cumulative)

```
day-01  scaffold + /health
day-02  domain + EF schemas
day-03  REST /api/v1 + problem+json (Ordering) + state machine
day-04  idempotency, xmin→409, domain events (+ coupons island → main)
day-05  indexes, pool, read replica (+ cursor history → main)
day-06  Redis cache, stampede lock, SSE  (+ LB/ETag/RL/edge → main)
day-07  Polly + modular Payment + async payment
day-08  extract Payment service + own DB  →  /api/v1/payments (canonical)
day-09  Kafka + Outbox/Inbox + saga (payment-results)
        ★ Inbox stamped AFTER side effects (same rule forever after)
day-10  JWT + RBAC + PII
day-11  Delivery extract + gateway + refund compensation (Inline AcceptMode)
day-12  Restaurant extract + menu replica + DecisionMode=Service path
day-13  OTel traces across HTTP + Kafka + Outbox
day-14  breaker, backpressure, load-shed, canary
day-15  breadth / interview
day-16  load test + cost + portfolio
```

## Patterns that must stay consistent across days

Students should **never** see a “worse” version of a pattern on a later day than an earlier day.

| Pattern | Introduced | Invariant from that day forward |
|---------|------------|----------------------------------|
| URL grammar | Day 3 | Public paths are `/api/v1/{resource}` |
| Errors | Day 3 | `application/problem+json` (RFC 7807) |
| Money | Day 3 | `{ amount, currency }` never naked decimals |
| Inbox | Day 9 | **Check → do work → stamp inbox → commit offset** |
| Outbox | Day 9 | Same DB transaction as the business write |
| Traceparent | Day 13 | On Outbox rows + Kafka headers when OTEL on |
| Health | Day 1 / refined | `/health` live, `/health/ready` deps (DB) |

## Dual modes (levers) — not two competing designs

Some knobs look like “two architectures.” They are **teaching levers**, not product forks:

| Lever | Default | Why two values exist |
|-------|---------|----------------------|
| `Restaurant:DecisionMode` | `Inline` | Tests + Day 11 (no Restaurant consumer). `Service` = Day 12+ multi-service truth. |
| `Restaurant:AcceptMode` | `Auto` | `Reject` earns refund saga (ADR-045/062). |
| `Ordering:RestaurantReadMode` | `LocalReplica` | `SyncHttp` is the **anti-pattern** demo (Day 12 wound). |
| `Saga:Mode` | `Choreography` | `Orchestration` = same writes + named steps + `saga_instances`. |
| Payment dual route | both | `/api/v1/payments/*` canonical; `/payments/*` kept as expand dual-route until fully contracted. |

**Day 11 class:** leave `DecisionMode=Inline`.  
**Day 12 class:** set Ordering `DecisionMode=Service` and run Restaurant.Api with Kafka.  
**Unit tests:** always Inline (no Kafka required).

## Why Day 6 does not have Restaurant.Api

Day 6 is **cache + SSE** inside the monolith. Restaurant extraction is earned on Day 12 when pricing on the critical path forces a read model. If you open Day 6 code looking for `Tadka.Restaurant.Api`, you are on the wrong branch for that concept — use Day 12+.

## Why Day 9 Inbox looks “simple”

Day 9 introduces Inbox. The **correct order** (effect then inbox) is part of the Day 9 teaching bar and is kept on every later branch. If an older island still stamped inbox first, that was a bug — fixed and forward-ported.

## Instructor checklist when changing a cross-day pattern

1. Fix on `main`.
2. Fast-forward `day-12`…`day-16` to `main` (cumulative full stack).
3. If the pattern first appears on day-N (e.g. day-09 Inbox), cherry-pick or merge the fix onto `day-N`…`day-11` too.
4. Update this file + the day’s runbook in the same commit.
