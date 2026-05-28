# Tadka - Copilot Custom Instructions

## Project Context
Tadka is a food delivery platform built as a teaching tool for the Desi Architect cohort. It starts as a .NET 10 monolith and evolves into a modular monolith, then microservices over 8 weeks.

The system handles: restaurant listings, menu management, order placement, delivery tracking, user accounts, and payments.

## Current Architecture
- **Single project:** `Tadka.Api` (.NET 10 Web API)
- **Database:** PostgreSQL 16 via EF Core (Npgsql provider)
- **Domain folders:** Orders, Restaurants, Delivery, Users, Payments
- **Pattern:** Vertical slice inside domain folders. Each domain folder contains its own controllers, services, entities, and DTOs.

## Coding Standards

### General
- Use `async`/`await` for all I/O operations. No `.Result` or `.Wait()`.
- Nullable reference types enabled (`<Nullable>enable</Nullable>`)
- Use Controllers, not Minimal APIs
- File-scoped namespaces (`namespace Tadka.Api.Domain.Orders;`)
- One class per file (except small DTOs that belong together)

### Naming
- PascalCase: public members, types, namespaces, methods
- camelCase: private fields (prefix with `_`, e.g., `_orderRepository`)
- I-prefix for interfaces: `IOrderService`, `IOrderRepository`
- Suffix conventions: `*Controller`, `*Service`, `*Repository`, `*Dto`, `*Entity`

### Error Handling
- Return RFC 7807 Problem Details for all errors
- Use `Results.Problem()` or custom `ProblemDetails` responses
- Don't throw exceptions for business logic failures. Use Result pattern or return Problem Details.
- Log exceptions with structured logging (Serilog)

### Database (EF Core)
- Code-first migrations
- Fluent API for configuration (not data annotations)
- Schema per domain: `orders`, `restaurants`, `delivery`, `users`, `payments`
- Use `IQueryable` in repositories, materialize in services

### Testing
- xUnit for all tests
- FluentAssertions for readable assertions
- NSubstitute for mocking
- Testcontainers for integration tests (real PostgreSQL)
- Test naming: `MethodName_Scenario_ExpectedBehavior`

### API Design
- RESTful conventions: `GET /api/restaurants`, `POST /api/orders`
- Use `[ApiController]` attribute
- Return `ActionResult<T>` from controller methods
- Pagination: `?page=1&pageSize=20` with `X-Total-Count` header
- API versioning via URL path: `/api/v1/`

## Architecture Rules
- **No premature optimization.** Don't add caching, message queues, or service extraction until the codebase demonstrates the need.
- **Domain boundaries are folder boundaries.** Cross-domain communication goes through service interfaces, not direct entity references.
- **Each domain owns its data.** No shared tables across domains. If Delivery needs an Order, it calls `IOrderService`, not `OrderEntity` directly.

## What NOT to Generate
- Don't add Redis, Kafka, or API Gateway code unless explicitly asked
- Don't add MediatR or CQRS patterns (those come in Week 4)
- Don't create microservice projects (the monolith is intentional)
- Don't add authentication/authorization (comes in Day 4)
- Don't add Terraform or CI/CD (comes in Day 12)
