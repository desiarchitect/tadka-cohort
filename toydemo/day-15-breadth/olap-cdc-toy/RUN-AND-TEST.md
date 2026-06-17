# RUN-AND-TEST.md for OLAP / CDC Toy

**Real-only demo** — Postgres fact scan vs rollup + optional CDC jsonl relay.

## Run
```powershell
docker compose up -d postgres
cd tadka\toydemo\day-15-breadth\olap-cdc-toy
node real-db.js --mode=break
node real-db.js --mode=fix
node real-cdc.js
```

## Observe
| Mode | Rows touched | Execution time (typical) |
|------|----------------|--------------------------|
| break (fact) | ~116k in 7-day window | ~27ms |
| fix (rollup) | ~30 rows | ~0.15ms |

## CDC beat
`real-cdc.js` applies jsonl order events to `analytics_rollup` without scanning `analytics_fact`.

## Narrative
OLTP Postgres is not your warehouse. CDC maintains rollups; columnar is the scale-out version.