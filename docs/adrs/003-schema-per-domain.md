# ADR-003: Schema-Per-Domain in a Single Database

**Date:** 2026-05-30
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Tadka has 5 domain areas (ordering, restaurant, delivery, identity, payment) all running in a single PostgreSQL 16 database as part of our monolith-first strategy (ADR-002). We need to decide how to organize tables within that database. The choice affects how easily we can extract services later and how much discipline the team needs day-to-day.

We have 9 tables today and expect 15-20 by the time all domains are built out. The team of 6 works across all domains, and we need clear ownership boundaries without the operational overhead of multiple databases.

## Decision

Create one PostgreSQL schema per domain area: `ordering`, `restaurant`, `delivery`, `identity`, `payment`. All schemas live in the same database. Tables are qualified: `ordering.orders`, `restaurant.restaurants`, `restaurant.menu_items`, etc.

In EF Core, each entity configuration specifies its schema via `ToTable("orders", "ordering")`.

## Consequences

### Positive

- **Logical isolation from day one.** `ordering.orders` can't accidentally reference `restaurant.restaurants` with a FK because that's a cross-schema FK we've banned (see ADR-008). Developers naturally think in domain boundaries.
- **Clean extraction path.** When we extract services in Week 4+, each service takes its schema. The migration path is: schema → separate database → separate service. No table-shuffling needed.
- **Minimal operational overhead.** One database, one connection pool, one backup, one set of credentials. We get domain boundaries without the cost of separate databases (no cross-database JOINs to worry about, no distributed transactions).
- **Discoverability.** `\dt ordering.*` in psql shows you exactly what the ordering domain owns. New developers understand ownership instantly.

### Negative

- **EF Core configuration overhead.** Every entity needs an explicit `ToTable("name", "schema")` call. Without it, everything lands in the default schema. More configuration than a single shared schema.
- **Schema creation in migrations.** First migration must create all schemas (`CREATE SCHEMA IF NOT EXISTS ordering`). Minor ceremony but easy to forget.

### Risks

- **Risk:** Developers bypass schema boundaries through shared queries or direct SQL. **Mitigation:** Code reviews enforce the rule. No cross-schema JOINs in application code. If you need data from another domain, call a method on that domain's service class.
- **Risk:** Schema count grows if sub-domains proliferate. **Mitigation:** Limit to one schema per bounded context, not per feature. 5 schemas is the target.

## Alternatives Considered

### Option A: All Tables in One Schema (public)
- Pros: Zero configuration. Default EF Core behavior. No schema management.
- Cons: No logical separation. Developers add cross-domain FKs casually ("just add a foreign key to users"). Extraction later requires figuring out which tables belong to which domain. "Just add a FK" laziness leads to a tangled schema.
- Why rejected: Shared schema makes the monolith harder to split. We're investing in extraction readiness from day one.

### Option B: Separate Database Per Domain
- Pros: Strongest isolation. Each domain is independently scalable, backed up, and migrated.
- Cons: Overkill for a monolith. No cross-database JOINs in PostgreSQL (need `postgres_fdw` or application-level joins). Distributed transactions for operations that span domains (placing an order touches ordering + restaurant + payment). 5 connection strings, 5 backup schedules, 5 migration histories for 6 engineers and 0 users.
- Why rejected: We're a monolith. Separate databases solve a problem we don't have yet and create problems we can't afford.

## References

- ADR-002: Start with a Monolith (this decision follows from it)
- ADR-008: No Cross-Schema Foreign Keys (the enforcement rule for this decision)
- PostgreSQL documentation: [Schema Search Path](https://www.postgresql.org/docs/current/ddl-schemas.html)

## Revisit When

When load testing or business requirements prove this decision is a bottleneck, or when specific pain points mentioned in 'Risks' are realized.
