# Tadka: instructions for AI coding agents

This file is the **single source of truth** for every AI tool in this repository. GitHub Copilot, Cursor and Codex read `AGENTS.md` directly. Claude Code reads it through one line in `CLAUDE.md`: `@AGENTS.md`.

## What this repository is

A food delivery backend that grows from one .NET 10 monolith into **four services plus a gateway** over 16 days. It is a **teaching repository**: the inline `// (ADR-NNN)` comments are deliberate. Never remove or shorten them.

| Piece | Project | Port | Database |
|---|---|---|---|
| API Gateway (YARP) | `src/Tadka.Gateway` | 8080 | none |
| Orders + Identity | `src/Tadka.Api` | 5224 | Postgres 5432 (replica 5433), schemas `ordering` and `identity` |
| Payment | `src/Tadka.Payment.Api` | 5240 | Postgres 5434 |
| Delivery | `src/Tadka.Delivery.Api` | 5250 | Postgres 5435, Redis geo for live location |
| Restaurant | `src/Tadka.Restaurant.Api` | 5260 | Postgres 5436 |

Shared infrastructure: Kafka 9092 (SASL), Redis 6379, Kafka UI 8090. Tests live in `tests/Tadka.<Project>.Tests`.

## Commands (run them in PowerShell on Windows)

```powershell
dotnet build Tadka.slnx
dotnet test                                    # about 207 tests, about 90 seconds (Testcontainers)
dotnet test tests/Tadka.Api.Tests --filter "FullyQualifiedName~BoundaryTests"
docker compose up -d                           # never run "docker compose down -v": it deletes the data
```

Before you say a task is done, `dotnet build Tadka.slnx` and the relevant `dotnet test` must be green. Show the output.

## Architecture rules (do not break these)

1. **Schema per domain, no cross-schema foreign keys (ADR-003, ADR-008).** Cross-domain references are plain ids (`Guid RestaurantId`), never a navigation property or a foreign key. `BoundaryTests.cs` fails the build if you break this.
2. **Orders never references Payment (ADR-022, ADR-024).** The two talk only through Kafka events.
3. **Outbox, not dual writes (ADR-028).** An event is added to the same `DbContext` and saved in the same transaction as the business change:
   ```csharp
   _db.Set<OutboxMessage>().Add(new OutboxMessage
   {
       Topic = Topics.OrderPlaced, Key = order.Id.ToString(), Payload = json,
       TraceParent = Tadka.Telemetry.TadkaTrace.CurrentTraceParent()   // ADR-041
   });
   await _orderRepository.SaveChangesAsync();
   ```
   The relay claims rows with `FOR UPDATE SKIP LOCKED`. Consumers are idempotent through an inbox table.
4. **Prices come from a local read model (ADR-037).** Orders prices an order from `ordering.menu_replica` through `IRestaurantPricingSource`. It never calls the Restaurant service on the order path.
5. **Payment resilience (ADR-021, ADR-043).** Bulkhead 10, retry 2 (jittered, transport errors only), circuit breaker (ratio 0.5, minimum 5 calls, 30 s window, 60 s open), 2 s timeout per attempt. A **decline** (`PaymentDeclinedException`) is final: never retried, never counted by the breaker. An **outage** (`PaymentGatewayUnavailableException`, `TimeoutRejectedException`) is retried and counted.
6. **Dependency classes (ADR-044).** Redis and the read replica are performance dependencies: fall back to the database. Postgres and money are correctness dependencies: fail loudly, never serve stale money.
7. **Metrics use low-cardinality labels only (ADR-042).** Order ids and user ids go on spans and logs, never on Prometheus labels.
8. **Auth is checked in every service (ADR-030, ADR-031, ADR-067).** The gateway is not a trust boundary. Services that use `[Authorize(Roles = ...)]` set `MapInboundClaims = false`, or the `role` claim is never seen. Ownership: a customer can read only their own orders (`if (!User.IsAdmin() && order.CustomerId != User.UserId()) return Forbid();`), and a restaurant owner only their own restaurant (`OwnsOrAdmin` in `RestaurantsController`).

## Coding conventions

- `Tadka.Api` and `Tadka.Restaurant.Api` use **Controllers** (`[ApiController]`, explicit routes such as `[Route("api/v1/orders")]`). `Tadka.Payment.Api` and `Tadka.Delivery.Api` use small minimal endpoints in `Program.cs`. Copy the style of the project you are editing.
- Errors are RFC 7807 Problem Details (ADR-006). In controllers call `Problem(...)`. Malformed input is 400, missing is 404, a broken business rule is 422, a lost race is 409.
- C# 14 on .NET 10, nullable reference types on, file-scoped namespaces.
- EF Core: Fluent API in `IEntityTypeConfiguration<T>` (no data annotations), schema named in `ToTable("name", "schema")`, optimistic concurrency with the `xmin` column (ADR-012).
- Tests: xUnit, FluentAssertions, NSubstitute (never Moq), Testcontainers through the `*ApiFactory` fixtures. Test names are readable sentences in snake case, for example `Cancel_from_Created_succeeds_with_reason`.
- Order states are `Created`, `Confirmed`, `Preparing`, `ReadyForPickup`, `PickedUp`, `Delivered`, `Cancelled`. Cancel is allowed only from `Created` or `Confirmed`. `OrderStateMachine.cs` is the truth.

## How to work

1. **Design first.** For a new decision, write an ADR in `docs/adrs/` before code. The ADRs run 001 to 067 (there is no 046), so the next one is 068. Use `docs/templates/adr-template.md`.
2. Read the closest existing file before you write a new one, and copy its structure.
3. Do not add a NuGet package without asking, and say why.
4. If the code and an ADR disagree, stop and ask. Do not "fix" the ADR to match.
