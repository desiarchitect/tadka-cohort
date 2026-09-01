# Tadka 🍛

A food delivery platform built as a teaching project for the [Desi Architect](https://desiarchitect.com) cohort.

This branch is **Day 6**: Day 5's replica plus **Redis cache-aside** on the menu, a **single-flight stampede lock**, and **SSE live tracking** over a Redis pub/sub backplane. HTTP caching, nginx scale-out, and rate limiting are **on the branch** and **not** Sunday's lecture.

## Where this is going

```
Client  →  Tadka.Api (/api/v1)  →  PostgreSQL 16 primary  :5432
                                 →  PostgreSQL 16 replica  :5433
                                 →  Redis 7                 :6379
```

No Kafka, gateway, or Payment HTTP API yet.

## Getting started

```powershell
git checkout day-06
docker compose down -v
docker compose up -d
docker exec tadka-redis redis-cli ping     # PONG
dotnet test Tadka.slnx                     # 27/27
dotnet run --project src/Tadka.Api
curl.exe http://localhost:5224/health
```

HTTP **5224**. Three containers. Full curls: [`docs/runbooks/day-06.md`](docs/runbooks/day-06.md). `curl.exe`, `"@docs/runbooks/place-order.json"`.

## Taught today

| ADR | What |
|---|---|
| 018 | Cache-aside menu, TTL ~60s, delete-on-write. Redis down → menu still **200**. |
| 019 | `SET lock:{key} NX EX` single-flight. |
| 020 | `GET /api/v1/orders/{id}/events` + Redis channel `order:{id}`. Redis down → SSE **503**. |

**Leftover (break-kit / weekday):** 047 nginx, 048 ETag/gzip, 049 rate limit, 050 edge+signed URLs, 051 SSE replay. Refer by **topic and number**.
