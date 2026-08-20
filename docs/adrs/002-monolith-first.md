# ADR-002: Start with a Monolith

**Date:** 2026-05-30
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Tadka is a greenfield food delivery platform launching in Bangalore with a team of 6 engineers and 3 months of runway to MVP. We need to decide on the initial application architecture: monolith vs microservices.

The founding team has experience at Swiggy and Dunzo, where they saw both monolithic and microservices architectures at various stages. Every one of those companies started as a monolith.

We have zero users, zero traffic, and zero production data. We don't know which parts of the system will become bottlenecks. Guessing wrong with microservices upfront means we burn weeks on infrastructure instead of shipping features.

## Decision

Build Tadka as a single .NET 10 Web API (`Tadka.Api`) backed by a single PostgreSQL 16 database. One deployable unit. When we model the domain, bounded contexts will live as folders *inside this project* — not as separate services. We do not pre-create those folders before we have named the contexts.

**What this looks like (today):**
- One `Tadka.Api` project (`Controllers/` + empty `TadkaDbContext`)
- One Docker Compose file (PostgreSQL)
- One CI/CD pipeline
- One deployment target

**What this does not include yet:** domain folders, schema-per-domain, or entities. Those are the next design session, still inside this same project.

**What we explicitly avoid:**
- Separate services for each domain
- Inter-service communication (gRPC, HTTP, message queues)
- Service discovery, API gateway, distributed tracing
- Multiple databases

## Consequences

### Positive
- Ship MVP in weeks, not months. One codebase, one deploy, one database to manage.
- Debugging is straightforward. A request starts and ends in the same process. Stack traces are complete. No distributed tracing needed yet.
- Schema changes are simple. One migration, one rollback. Foreign keys work across domains because it's all one database.
- The team of 6 can all work in one repo without coordination overhead of service boundaries.
- Refactoring is cheap. Moving code between domain folders is a file move, not a service extraction.

### Negative
- All domains share a single deployment. A bug in restaurant management takes down order placement too.
- Single database means one connection pool, one set of indexes, one backup strategy for everything.
- The codebase will grow. Without discipline, domain boundaries in folders will blur.
- When we need to scale one domain independently (say, delivery tracking needs real-time WebSocket connections), the monolith makes that harder.

### Risks
- **Risk:** Team treats folder boundaries casually, domains become tangled, extraction becomes painful later. **Mitigation:** Code reviews enforce that domain folders don't import from each other directly. Shared types go in a Common/ folder.
- **Risk:** Single database becomes a bottleneck at scale. **Mitigation:** We'll add read replicas (Week 2-3), then extract hot domains when load testing proves the bottleneck (Week 4+).

## Alternatives Considered

### Option A: Microservices from Day 1
- Pros: Each domain independently deployable, scales independently, technology flexibility per service.
- Cons: 6 engineers managing 5 services + API gateway + message broker + service discovery + distributed tracing + 5 CI/CD pipelines for an app with 0 users. We'd spend month 1 on infrastructure and ship 0 features.
- Why rejected: Premature complexity. We have no data on which domains need independent scaling. Swiggy ran as a monolith until they had 10K+ daily orders. We should do the same.

### Option B: Modular Monolith from Day 1
- Pros: Stronger domain isolation than plain folders (separate assemblies, explicit interfaces). Easier extraction later.
- Cons: More upfront ceremony (interfaces, dependency injection configuration, project references). Slower to iterate in the first few weeks when we're still discovering domain boundaries.
- Why rejected: We'll evolve to this in Week 4 when we introduce MediatR and domain events. Starting with it adds friction before we understand our domains well enough.

## References
- [MonolithFirst by Martin Fowler](https://martinfowler.com/bliki/MonolithFirst.html)
- Swiggy engineering blog: started as a single Django monolith, split after 2 years
- Shopify: famously runs a modular monolith at massive scale
