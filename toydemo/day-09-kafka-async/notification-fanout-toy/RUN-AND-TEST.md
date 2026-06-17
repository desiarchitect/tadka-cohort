# RUN-AND-TEST.md for Notification / Promotional Fan-Out Toy

**Toy:** Notification / Promotional Fan-Out Toy
**Day Introduced:** Day 09 (Kafka, outbox/inbox, ADR-027–029)
**Related Curriculum:** Day 9 saga/outbox/inbox, promotional vs transactional notification contrast, interview fan-out problems.
**Purpose:** Failure-first demo of synchronous mass push (timeout + missed users + duplicate retries) vs async Kafka fan-out with inbox idempotency and DLQ.

## 1. Overview & Why This Toy Exists
Tadka Day 9 builds **transactional** async (order-placed → payment) with outbox/inbox. Mass promo push is a different beast: **10k–1M recipients**, at-least-once delivery, poison devices, and an admin API that must **not** block.

This toy shows why "just loop `sendPush()` in the controller" fails and how the Day 9 inbox pattern scales to fan-out.

## 2. The Failure Scenario
**Workload:** Marketing triggers "50% off biryani" to 10,000 users from the admin API.

**Naive pattern:** `for (user of users) { await pushProvider.send(user); }` inside the HTTP handler.

**Bad symptoms:**
- 10,000 × 2ms = 20s — client/gateway timeout at ~5s
- Only ~2,500 users notified; 7,500 missed
- Ops retries → duplicate pushes to first batch
- One slow/invalid token can block the loop if not handled

## 3. Exact Steps to Induce & Observe the Break
```powershell
cd tadka\toydemo\day-09-kafka-async\notification-fanout-toy
node index.js --mode=break
```

**Observe:**
- `Client timed out: YES`
- `Pushes delivered: ~2,500`
- `Users missed: ~7,500`
- `Duplicate pushes on retry: ~2,500` (if ops retries)

## 4. The Fix
1. Admin API writes one outbox row / publishes one `promo.scheduled` Kafka event → **202 Accepted** in ms.
2. Worker pool fans out with bounded concurrency.
3. **Inbox dedupe key** `campaignId:userId` — redelivery does not double-notify (ADR-028).
4. **DLQ** after retries for permanent failures (invalid push token).

```powershell
node index.js --mode=fix
```

## 5. Steps to Verify the Fix
| Metric | break | fix |
|--------|-------|-----|
| Client timeout | YES | no |
| Users notified | ~2,500 | ~9,800 (2% invalid → DLQ) |
| Duplicates on retry | thousands | 0 |
| Inbox deduped on redelivery | — | >0 (simulated 10% redelivery) |
| Wall time | ~5,000ms (timeout) | ~400ms order-of-magnitude |

**Real Kafka (optional):**
```powershell
cd tadka
docker compose up -d kafka
cd toydemo\day-09-kafka-async\notification-fanout-toy
npm install
node real-kafka.js --mode=fix
```

## 6. Full Run Instructions
```powershell
node index.js --mode=break
node index.js --mode=fix
```

No Docker required for simulation. Kafka optional for `real-kafka.js`.

## 7. Test Cases & Expected Results
| Test | Command | Broken | Fixed |
|------|---------|--------|-------|
| Sync fan-out | `index.js --mode=break` | timeout + ~75% missed | — |
| Async + inbox | `index.js --mode=fix` | — | ~98% delivered, dedup on redelivery |
| Real Kafka | `real-kafka.js` | — | 1 message, 500 unique sends, dedup > 0 |

## 8. Troubleshooting
- **Kafka connection refused** — run `docker compose up -d kafka` from `tadka/` (no `#` comments on Windows).
- **Fix mode random variance** — redelivery % is probabilistic; re-run, shape should hold.
- **real-kafka slow first run** — topic/group creation; wait for broker healthy.

## 9. Cross-Stack Notes
- **Tadka Day 9:** transactional outbox/inbox for order/payment — same dedupe primitive.
- **Java:** Spring Kafka + idempotent consumer or inbox table.
- **Node:** BullMQ / Kafka consumer groups with dedupe store in Redis/Postgres.

## 10. Curriculum Links
- ADR-027 Kafka backbone, ADR-028 outbox/inbox, ADR-029 saga
- Contrast: transactional (order-confirmed) vs promotional (this toy)
- Razorpay teardown: webhooks = at-least-once + inbox

## 11. Failure-First Narrative
"Marketing wants a push to ten thousand users. The intern puts a for-loop in the admin controller. Twenty seconds later the gateway returns 504. Only twenty-five hundred users got the promo. Marketing hits retry — those twenty-five hundred get it twice. The fix is boring: one durable event, workers fan out, inbox dedupe so Kafka redelivery doesn't spam, DLQ for dead tokens. Admin gets 202 in fifty milliseconds."

## 12. Limitations
- Does not implement real FCM/APNs providers.
- Simulation uses sleep not real push API.
- Does not cover segment query (SQL → user list) — focuses on fan-out mechanics.

---

*Teaching flow:* break numbers first → connect to Day 9 inbox → optional Kafka UI at localhost:8080 (if kafka-ui profile up on your compose).