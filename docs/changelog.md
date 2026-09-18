# Day 7 changelog (since Day 6)

`git checkout day-07`. Previous branch: `day-06`.

## We learned

- An unbounded call to something you do not own is a **brownout**, not a crash.
- **Timeout** bounds one call. **Bulkhead** (`queueLimit: 0`, cap 10) bounds how many run at once. 10 parallel Slow charges complete; 100 → ~10 complete, ~90 `RateLimiterRejectedException` in ms. HTTP is still **201** — the order is created first.
- **Async payment** (CQRS-lite): `POST /orders` returns 201 immediately; a Channel + BackgroundService charges later. SSE notifies; `payment.payments` is the truth.
- **Modular monolith:** Payment owns `PaymentDbContext` / `payment` schema / own migration history. Ordering has zero Payment types (grep).
- **Redis in production (opener):** cache vs SSE vs session classification; replica is a copy not failover; Cluster is 16384 slots (`MOVED`) not HA; Sentinel (or ElastiCache) is how failover is set up. Same seat / two countries → one inventory writer, not two Redis.

## Architecture

- Still **one process**. Payment is a **module**, not a service (Day 8).
- Polly pipeline around the fake gateway: bulkhead → timeout (retry/breaker come Day 14).
- `Payment:Mode` Sync | Async | Off; gateway Fast | Slow | Failing.
- Redis still standalone `tadka-redis`. HA topologies live in `toydemo/day-07-redis-ha/`, not in Tadka compose.

## Code vs Day 6

| Area | What changed |
|---|---|
| `Modules/Payments/*` | Fake gateway, processor, work channel, MediatR `OrderPlaced` handler |
| `Infrastructure/Resilience/PaymentResiliencePipeline.cs` | Timeout + concurrency limiter |
| `payment` schema | Own `__EFMigrationsHistory` |
| `RedisCacheService` | Lock release is **Lua compare-and-delete** (ADR-019), not GET-then-DEL |
| `docs/runbooks/day-07.md` | Brownout + bulkhead burst + Redis production §0b |
| `toydemo/day-07-redis-ha/` | Replica, 3-node Cluster, Sentinel toy |
| `docs/learn/redis-in-production.md` | Student one-pager |
| `docs/demo-scripts/06-bulkhead-burst.ps1` | 10 vs 100 parallel POSTs |

ADRs **021, 022, 023** (new). ADR-019 updated for Lua.
