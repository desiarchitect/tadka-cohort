# Redis in production (Day 7 opener)

Day 6 taught **commands** and cache-aside. This page is **what you do when Redis is real traffic**. Tadka still ships **one** container (`tadka-redis` :6379). Replica, Cluster, and Sentinel live in [`toydemo/day-07-redis-ha/`](../../toydemo/day-07-redis-ha/).

Live script: runbook [`day-07.md`](../runbooks/day-07.md) section **0b**.

## Classification is per feature, not per tool

| If Redis holds… | Redis down means… | Production bar |
|---|---|---|
| Cache (Tadka menu) | Slower, still correct. HTTP **200** | Replica optional. Miss-storm to DB is the risk |
| Stampede lock | Two refreshers, not a herd if TTL is short | Same as cache |
| Pub/sub backplane (SSE) | No live stream. Honest **503** | HA **or** a second path. Do not hang |
| Session (not Tadka — JWT is Day 10) | Everyone logged out | Redis is a **correctness** dep → HA required |
| Rate limit (leftover on this branch) | We **fail open** | Fail closed = outage. Product call |

Orders and payments do **not** use Redis (Postgres). Money never lives here.

## How each topology is set up

| Topology | What you actually configure | Gives you | Does **not** |
|---|---|---|---|
| Standalone (Tadka) | one `redis-server` | Dev | Survive a node death |
| Replica | `--replicaof host 6379` | A **copy** for extra reads | Automatic failover. App still points at the dead master |
| Cluster | `cluster-enabled yes` then `redis-cli --cluster create <ip>:6379 … --cluster-replicas 0`. Redis 7: `cluster-announce-ip` must be a **literal IP**, not a Docker hostname | 16384 **slots**, `MOVED`, scale memory | HA, unless `--cluster-replicas 1` (or more) |
| Sentinel | `sentinel monitor mymaster <ip> 6379 <quorum>` — use a **static IP**. Hostname + Docker DNS NXDOMAIN on `docker stop` puts Sentinel in TILT | **Promotes** a replica; client follows Sentinel | Freedom from ops. Still one writer |
| Managed | ElastiCache / Azure Cache / Memorystore **primary endpoint** | They run Sentinel-or-Cluster | Free. You still classify the *feature* |

Java: Redisson / Lettuce. Node: ioredis. Go: go-redis. Same topologies.

**Revisit (ADR-018):** if Postgres can no longer absorb a full cache miss-storm, Redis becomes a hard dependency and you **buy** Sentinel, Cluster+replicas, or managed.

## Two countries, same seat (interview)

This is **not** a Cluster question. Cluster is one region. Mumbai Redis and London Redis are two caches. Async replica across the ocean can `GET seat:12A` = free on both sides → **double booking**.

Fix: **one inventory writer** — unique `(flight, seat)` row, `UPDATE … WHERE free RETURNING`, hold with TTL. Regional Redis may cache the map. The book click always hits the seat service. RTT from the US to India is the cost of one seat. CRDT / last-writer-wins **cannot** be a seat.

Tadka: cache the **menu** per region if you want. Never cache **order status** or “this seat”.
