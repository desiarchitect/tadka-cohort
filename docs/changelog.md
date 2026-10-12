# Day 15 changelog (since Day 14)

`git checkout day-15`. Previous branch: `day-14`.

## We learned

- An interview is an ADR performed in 45 minutes: Clarify (5) → Estimate (5) → HLD (10) → Deep Dives (15) → Bottlenecks/Failure (7) → Wrap (3).
- Map every system design claim to runnable evidence in Tadka: idempotency (Day 4), outbox catch-up (Day 9), geo dispatch (Day 11), local read models (Day 12), distributed tracing (Day 13), resilience / circuit breakers (Day 14).
- Breadth topics and patterns: full-text search / inverted indexes, video streaming CDN/HLS, OLAP/CDC rollups, object storage pre-signed URLs, web crawling, and CRDT counters.

## Architecture

- Teaching & interview-transfer milestone: no application code changes.
- System operates the canonical 4 services + gateway with full persistence, messaging, observability, and resilience.
- Breadth toys verified in `toydemo/` (13 toys across Days 03, 06, 09, 15).

## Code vs Day 14

| Area | What changed |
|---|---|
| Application code | None (zero code changes; teaching-only milestone) |
| Documentation | New student runbook `docs/runbooks/day-15.md` |
| ADR Canon | 66 ADR files, numbered 001-067 (no 046) |
| Tests | Unchanged from Day 14 (5 test projects) |

ADRs through **067** (046 unused).
