# Tadka - Copilot Custom Instructions

## Project Context

Tadka is a food delivery platform built as a teaching tool for the Desi Architect cohort. **Today it is a .NET 10 monolith** — one API project, one PostgreSQL database, five domain folders, schema-per-domain.

Restaurant and order HTTP APIs exist under `/api/v1`. The client never sends a price — `OrderFactory` prices from the current menu. No Payment endpoints yet.

## Current Architecture

- **Single project:** `src/Tadka.Api` (.NET 10 Web API, Controllers)
- **Database:** PostgreSQL 16 via EF Core (Npgsql). `Program.cs` applies migrations on startup.
- **Schemas (not table prefixes):** `ordering`, `restaurant`, `delivery`, `identity`, `payment` — never `orders`/`users`.
- **Domain folders:** `Domain/{Orders,Restaurants,Delivery,Users,Payments}` plus `Domain/ValueObjects/{Money,Address,GeoLocation}`. Folder `Users` maps to schema `identity` — that mismatch is intentional (aggregate name vs bounded-context name).
- **Controllers** live in `Controllers/`, not inside domain folders.
- **Health:** `GET /health` is liveness only. `GET /health/ready` checks Postgres via `TadkaDbContext` (200 / 503).
- **Tests:** `tests/Tadka.Api.Tests` (xUnit)
- **ADRs in force:** 001–010 (REST `/api/v1`, RFC 7807, two-layer validation, no PUT/DELETE)
- **Do not** add Redis, Kafka, a Payment controller, `IOrderRepository`, or extra `src/` projects.

## Coding Standards

- Use `async`/`await` for all I/O. No `.Result` or `.Wait()`.
- Nullable reference types are enabled.
- Use Controllers, not Minimal APIs.
- File-scoped namespaces (`namespace Tadka.Api.Domain.Orders;`).
- One class per file (except small DTOs that belong together).
- PascalCase for public members and types; camelCase private fields with `_` prefix (`_dbContext`).
- I-prefix for interfaces.
- Fluent API in `TadkaDbContext` (`ToTable("orders", "ordering")`). No data annotations for mapping.
- Value objects are C# records mapped with `OwnsOne` (columns on the parent table, not a join table).
- Cross-domain references are **Guid ids only**. No navigation properties and no FKs across schemas.

## What NOT to Generate

- Do not add Redis, Kafka, API Gateway, Terraform, k6, or extra `src/` projects.
- Do not add MediatR, CQRS, authentication, RFC 7807, or `/api/v1` controllers unless explicitly asked.
- Do not add cross-schema foreign keys.
- Do not put controllers inside `Domain/`.
- Do not change `/health` into a database check; readiness is `/health/ready`.
