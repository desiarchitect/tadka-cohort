# ADR-018: Redis Cache-Aside for Hot Reads (StackExchange.Redis)

**Date:** 2026-06-02
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Day 5's read replica spread read *volume* across two boxes, but it didn't remove *repeated* work. During the dinner rush every customer opens the same handful of popular restaurant menus, so the database (primary or replica) runs the **identical** `GET /restaurants/{id}/menu` query thousands of times a second. The menu changes maybe twice a day; re-reading it from a database on every request is the definition of read amplification. This is the next rung on the cheapest-first ladder (`scaling-decision-tree.md`), and the last optimization before we even consider extracting services.

We need an in-memory cache for hot, read-heavy, rarely-changing data — and a discipline for *what* to cache and *how to keep it correct*.

## Decision

**Introduce Redis and cache the menu read with the cache-aside pattern.**

- **Client:** **StackExchange.Redis** — one client for everything Redis (cache get/set, the stampede lock in ADR-019, and the pub/sub backplane in ADR-020). Wrapped behind a small **`ICacheService`** (`GetOrSetAsync<T>`, `RemoveAsync`) so call sites stay clean and tests can swap it.
- **Cache-aside flow:** check Redis → on miss, query the DB, populate Redis with a **TTL (~60s)**, return. On hit, the DB never sees the request.
- **Graceful no-op:** if no `Redis` connection string is configured, `ICacheService` becomes a pass-through (always-miss → DB). Single-Postgres dev and the test suite run unchanged — same pattern as the Day-5 replica fallback (ADR-016).
- **What to cache (the matrix):** cache only when **read:write ≫ 1 AND staleness is tolerable**. The **menu** qualifies (read constantly, written rarely, 60s stale is harmless). **Order status and payment do NOT** — they change often and staleness causes the exact duplicate-order / read-your-writes bug we guarded against on Day 4/5.
- **Invalidation: delete-on-write + TTL safety net.** When a menu item, its availability, or the restaurant changes (the existing PATCH/POST endpoints), **delete** the cache key; the next read repopulates from the DB. TTL bounds staleness if a delete is ever missed.

## Consequences

### Positive
- Hot menu reads are served from memory in sub-ms; the DB sees one query per TTL instead of thousands. The replica's CPU is freed for the unique-read long tail.
- One Redis client/skillset for cache + lock + pub/sub keeps the mental model small.
- The no-op fallback means Redis is a performance dependency, not a correctness one — the app still works (slower) if Redis is down.

### Negative / Risks
- A second datastore to run, monitor, and reason about (memory limits, eviction). Acceptable: it's the cheapest fix for the proven bottleneck.
- Stale window up to the TTL after a *missed* invalidation. Bounded and acceptable for the menu.

### Cost (₹ / effort)
One small Redis instance (cheap) + a thin cache service + a `DEL` (<1ms) on each menu write. Far cheaper than extracting services to shed read load.

## Alternatives Considered
- **Write-through (update Redis on every write):** adds latency to writes and, worse, if the cache update succeeds but the DB txn rolls back, the cache *lies*. Cache-aside **delete** is safer (worst case is a miss). Rejected.
- **`IDistributedCache` abstraction:** clean for swapping a memory cache in tests, but the stampede lock (ADR-019) and the pub/sub backplane (ADR-020) need StackExchange.Redis directly — two abstractions for one datastore. Rejected in favour of one client + `ICacheService`.
- **Cache everything:** invalidation complexity explodes and you cache data that changes constantly (order status) → stale-data bugs. Rejected for the read:write + staleness matrix.
- **TTL-only (no explicit invalidation):** a price change is wrong for the full TTL — customers see the old price, then get charged the new one. Rejected; TTL is the *safety net*, not the strategy.

## References
- ADR-016 (read replica — the rung below), ADR-004 (Postgres source of truth), ADR-013 (in-process events — reused for invalidation/backplane)
- `cohort-prep/day-02/datastore-selection.md` (Redis in the option space), `docs/scaling-decision-tree.md`, `docs/tadka-growth-story.md` (Week 2/3 caching)
- Implementation: `Infrastructure/Caching/ICacheService.cs` + `RedisCacheService.cs`, `Controllers/RestaurantsController.cs`

## Revisit When
Move invalidation to **event-driven (Kafka)** once an event bus exists (Week 5) — a `MenuUpdated` event a cache-invalidation consumer reacts to, decoupling the write path from cache concerns. Reconsider TTLs per data type if staleness complaints appear. Reconsider the no-op fallback if the DB can no longer survive a full cache outage (then Redis becomes a hard dependency and needs HA).
