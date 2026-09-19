# Day 8 — Options (not only .NET)

Tadka’s answers: extract **Payment**, talk **HTTP**, own **database**. This page is the **menu** — alternatives, cost, and the same move in Java / Node / Go.

ADRs: [024](../adrs/024-extract-payment-service.md) extract · [025](../adrs/025-sync-http-inter-service-bridge.md) HTTP · [026](../adrs/026-database-per-service.md) own DB.

## Same pattern in your stack

| Concept | .NET (Tadka) | Java / Spring | Node | Go |
|---|---|---|---|---|
| A standalone service | `Tadka.Payment.Api` | new Spring Boot app | Nest/Express in the monorepo | new Go module |
| Typed HTTP client | `IPaymentClient` + `HttpClient` | OpenFeign / RestClient | axios / fetch wrapper | `net/http` |
| Resilience on the hop | same Polly timeout + bulkhead | Resilience4j | opossum + AbortController | `context.WithTimeout` + semaphore |
| Contract (no shared code) | `ChargeRequest` copied per side | DTO per side | TS interface per side | struct per side |
| Own database | 2nd DbContext → Postgres `:5434` | 2nd DataSource | 2nd Prisma/TypeORM | 2nd `*sql.DB` |
| Own migrations | `Database.Migrate()` in the service | Flyway/Liquibase | Prisma migrate | golang-migrate |

## Decision A — extract, or not? (ADR-024)

| Option | When it fits | Cost | Switch trigger |
|---|---|---|---|
| Stay a modular monolith | no failure has earned the ops cost | lowest | Restaurant/Delivery stay until later weeks |
| **Extract Payment** (today) | fault / PCI / data isolation | med — process, DB, network | money + a clean seam |
| Extract everything first | almost never for a new product | very high | real org + scale, not vibes |

Latency is **not** the reason. Day 7 fixed slow **in one process**.

## Decision B — how services talk (ADR-025)

| Option | When it fits | Cost | Switch trigger |
|---|---|---|---|
| **HTTP/REST** (today) | first extraction; request/reply | low | reuse Day-7 Polly |
| gRPC | chatty internal hops; `.proto` | med | hot path / contract pressure |
| Async messaging (Kafka) | must not lose work when a peer is down | high — broker + idempotency | **Day 9** — earned by the lost charge |

HTTP’s cost is **temporal coupling**: peer down → call fails → queued charge is **lost**. That is runbook §3.

## Decision C — who owns the data (ADR-026)

| Option | When it fits | Cost | Switch trigger |
|---|---|---|---|
| Shared DB, separate schema | quick first step | low | still shared fate + PCI surface |
| **Own physical database** (today) | true service boundary; money | med — 2nd instance, no JOIN | payment is money |
| Different engine (ledger) | append-only compliance | high | a real ledger requirement |

No cross-service JOIN. Data moves via the contract/events, never a query.

## Four questions before you extract anything

1. What problem does this solve?
2. Does it exist **today**, with evidence?
3. Could it be solved **inside** the monolith? (if yes — do that first)
4. Is the **new** failure mode smaller than the problem?

All four. Three is not enough.
