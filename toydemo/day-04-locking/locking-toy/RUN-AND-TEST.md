# RUN-AND-TEST.md for Locking Toy

**Toy:** Pessimistic locking (FOR UPDATE blocking / NOWAIT / SKIP LOCKED)
**Day Introduced:** Day 4 (optimistic vs pessimistic engine room)
**Related Curriculum:** ADR-012 (orders use `xmin` / 409). This toy is the option we **rejected** for orders: exclusive row locks. Day 12 uses SKIP LOCKED for workers — Day 4 only shows the SQL primitive.
**Purpose:** Feel that a second `FOR UPDATE` **waits**, it does not error. Then fail-fast (`NOWAIT`) and skip (`SKIP LOCKED`).

This toy does **not** touch `ordering.orders`. Optimistic concurrency on orders is a separate Tadka curl (204 + 409).

## 1. Overview

Day 4 board work says: default `FOR UPDATE` **blocks**; `NOWAIT` errors; `SKIP LOCKED` takes a free row. Without two sessions against real Postgres, students think the second locker gets an exception.

## 2. The failure / the wait

Session A holds row 1. Session B asks for the same row with plain `FOR UPDATE`. B **freezes ~5 seconds**. No error. That wait is a held connection — the pool story if you then call a slow gateway (Day 7).

## 3. Exact steps (Windows PowerShell)

**Prerequisites:** Docker Desktop; from the Tadka repo root `docker compose up -d`; `tadka-postgres` **(healthy)**. Node.js. **Two** PowerShell windows.

In **both** windows:

```powershell
cd toydemo\day-04-locking\locking-toy
```

`hold` locks `id=1` for **5 seconds**, then **releases**. Start window B as soon as A prints `HOLD: locking id=1`. Do **not** wait for A to finish. **Restart `hold` before every B command** (`wait`, then `nowait`, then `skip` are three separate rounds).

**Round 1 — wait (queue, not an error)**

1. A: `node demo.js hold` → `HOLD: locking id=1 for 5 seconds` then **silence for ~5 s**.
2. B immediately: `node demo.js wait` → **also silence for ~5 s**, then `WAIT: got the lock` and `[client wall clock: ~5000ms]`. **No ERROR.**
3. A then prints `HOLD: released id=1`.

**Round 2 — nowait (fail-fast)**

1. A: `node demo.js hold` again.
2. B immediately: `node demo.js nowait` → **instant** `ERROR: could not obtain lock on row in relation "locking_demo"`. Wall clock tens of ms.

**Round 3 — skip (other row)**

1. A: `node demo.js hold` again.
2. B immediately: `node demo.js skip` → **instant** success, `id | label` = `2 | row-2`.

If B returns in ~100 ms with no freeze and no error, `hold` already finished. Start `hold` again and run B within one second.

**macOS/Linux:** same commands; path `toydemo/day-04-locking/locking-toy`.

## 4. The "fix" (pick the primitive)

| Mode | Meaning |
|---|---|
| `wait` (default FOR UPDATE) | Queue. Safe for rare collisions if the txn is short. Eats the pool if you hold the lock. |
| `nowait` | Fail-fast. Client retries or shows "busy". |
| `skip` | Workers: take a free row. Day 12 backfill uses this at scale. |

Orders in Tadka do **none** of these. They use `xmin` → 409.

## 5. Verify

| Test | Expected |
|---|---|
| `wait` while `hold` | Client wall clock ~5s; stdout shows the row; no ERROR |
| `nowait` while `hold` | `could not obtain lock`; exit non-zero |
| `skip` while `hold` | `id=2`; wall clock tens of ms |
| `wait` with no `hold` | Immediate; ~ms |

## 6. Full run

```powershell
docker compose up -d
docker compose ps          # tadka-postgres (healthy)
cd toydemo\day-04-locking\locking-toy
node demo.js hold          # other window: wait / nowait / skip
```

Cleanup: table `locking_demo` is tiny; optional `DROP TABLE locking_demo;` in psql. `docker compose down` wipes it with the volume.

## 7. Leftover — deadlock

Two windows, start together:

```powershell
node demo.js deadlock-a
node demo.js deadlock-b
```

One session: `ERROR: deadlock detected`. The other commits. Optional; not the Saturday never-cut.

## 8. Troubleshooting

| Symptom | What to do |
|---|---|
| `Cannot connect` / docker exec fails | `docker compose up -d`; service name `postgres`, container `tadka-postgres` |
| Container name in use | `docker compose down` in the other clone |
| `wait` returns instantly | `hold` already finished (5s). Start `hold` again, run `wait` within a second |
| `nowait` succeeds | Same — hold was not running |
| PowerShell `node` not found | Install Node, or run from a machine that has it |

## 9. Cross-stack

Same SQL in Java (`PESSIMISTIC_WRITE` / `LockModeType.PESSIMISTIC_WRITE`), Node (`SELECT … FOR UPDATE` in a transaction). `NOWAIT` and `SKIP LOCKED` are Postgres (and MySQL 8) syntax.

## 10. Curriculum

Day 4 teaching-script Beat 2 engine room. Day 4 runbook. **Do not run this on Day 3.** Cursor pagination toy is **Day 5**.

## 11. Instructor line

"Default FOR UPDATE wait karti hai — error nahi. NOWAIT turant fail. SKIP LOCKED doosri row. Orders pe hum yeh nahi lagate; wahan xmin aur 409 hai."

## 12. Limits

Does not demo a 50-client retry storm (that is `scripts/coupon-concurrency-demo.js`, leftover). Does not demo lock-across-SMS (`Demo:DispatchEventsBeforeCommit`, Day 7).
