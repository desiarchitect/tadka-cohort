# ADR-004: EF Core Code-First as ORM

**Date:** 2026-05-30
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Tadka's domain model is already defined in C# (Day 2). We need an ORM strategy to persist these domain objects to PostgreSQL. The choice affects development speed, query performance, and how well domain concepts (value objects, aggregates) map to the database.

Our team prioritizes development velocity over raw query performance at this stage. We're building a teaching codebase where students need to focus on domain logic, not SQL syntax. Tadka's initial scale (seed data, then maybe 1 lakh orders/day) is well within EF Core's performance ceiling.

## Decision

Use EF Core with Npgsql (PostgreSQL provider) in Code-First mode. Domain entity classes are the source of truth for the database schema. Migrations are generated from C# code and version-controlled. Use `OwnsOne` for value object mapping (Money, Address).

## Consequences

### Positive

- **Domain model IS the schema.** No separate SQL files to keep in sync with C# classes. Change a property, run `dotnet ef migrations add`, done. The migration is a version-controlled C# file.
- **Value object support via OwnsOne.** `Order.TotalAmount` (a Money value object) maps to `total_amount` and `currency` columns on the orders table without a separate table or JOIN. Clean DDD mapping.
- **LINQ for queries.** Developers write C# queries, not SQL strings. Compile-time type checking catches column name typos, type mismatches, and missing properties. IntelliSense works.
- **Migrations as code.** Every developer who runs `dotnet ef database update` gets the exact same schema. No "run this SQL script, then that one, in order" coordination.
- **Teaching advantage.** Students focus on domain logic instead of writing INSERT/UPDATE/SELECT by hand. EF Core handles the boring parts.

### Negative

- **Query overhead.** EF Core generates SQL that's 10-15% slower than hand-written SQL for complex queries. For JOINs across multiple entities with projections, the generated SQL can be suboptimal.
- **N+1 query traps.** Lazy loading or careless `.Include()` usage leads to N+1 queries. Requires developer awareness and code review.
- **Complex queries are harder.** Anything beyond basic CRUD (reporting queries, aggregations across domains) pushes against EF Core's LINQ-to-SQL translation limits.

### Risks

- **Risk:** Developers treat EF Core as a black box, never look at generated SQL, and ship slow queries. **Mitigation:** Enable SQL logging in development. Day 5 (indexing) will teach query analysis using EXPLAIN ANALYZE.
- **Risk:** Migrations diverge across developer machines. **Mitigation:** Single migration history in source control. Never generate migrations without pulling latest. CI pipeline validates migration consistency.
- **Risk:** EF Core's query translation chokes on complex LINQ expressions. **Mitigation:** Drop to raw SQL or Dapper for read-optimized paths. Day 7 (CQRS) will introduce Dapper for read models.

## Alternatives Considered

### Option A: EF Core Database-First
- Pros: Schema is designed in SQL (some teams prefer this). Works well when a DBA controls the schema.
- Cons: Schema and C# classes can drift. Regenerating classes from schema overwrites customizations. No natural value object mapping. The database leads the design instead of the domain.
- Why rejected: We want domain-driven design where C# classes lead. Code-first aligns with DDD.

### Option B: Dapper (Micro ORM)
- Pros: 10-15% faster query execution. Full control over SQL. No query translation surprises. Simpler mental model (write SQL, get objects).
- Cons: Every query is hand-written SQL. No migration support (need a separate tool like FluentMigrator or DbUp). Manual mapping for value objects. More code for the same CRUD operations.
- Why rejected: For a teaching project with 6 engineers, writing every INSERT/UPDATE/SELECT by hand is slow. We'll introduce Dapper for read models in Day 7 (CQRS) where EF Core's overhead matters.

### Option C: Raw ADO.NET
- Pros: Maximum performance, zero abstraction overhead, full control.
- Cons: Enormous boilerplate. Manual connection management, parameterized queries, result set mapping. No migration support. Every CRUD operation is 20-30 lines of code.
- Why rejected: We're building a domain-rich application, not squeezing microseconds. The productivity cost is unacceptable for a team of 6 on a 3-month timeline.

## References

- ADR-001: Use .NET 10 (EF Core is part of the .NET ecosystem choice)
- ADR-003: Schema-Per-Domain (EF Core's `ToTable` with schema parameter enables this)
- [EF Core Documentation: Value Objects](https://learn.microsoft.com/en-us/ef/core/modeling/owned-entities)
- [EF Core with Npgsql](https://www.npgsql.org/efcore/)

## Revisit When

When load testing or business requirements prove this decision is a bottleneck, or when specific pain points mentioned in 'Risks' are realized.
