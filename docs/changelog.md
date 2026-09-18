# Day 5 changelog (since Day 4)

`git checkout day-05`. Previous branch: `day-04`.

## We learned

- **Indexes** for the hot paths (EXPLAIN).
- **Connection pool** sizing — unbounded pool is a brownout waiting to happen (Day 7).
- **Read replica** + EF read/write split. Replica is a **performance** dep (fall through if down).
- Partitioning/sharding **deferred**.

## Architecture

- Postgres **primary :5432** + **replica :5433**. Still one app.

## Code vs Day 4

| Area | What changed |
|---|---|
| Migrations | Performance indexes |
| `TadkaReadDbContext` | Replica connection |
| `docker-compose.yml` | Streaming replica |
| Pool settings | Min/max |

ADRs **014, 015, 016, 017**.
