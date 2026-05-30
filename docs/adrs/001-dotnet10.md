# ADR-001: Use .NET 10 with ASP.NET Core

**Date:** 2026-05-30
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Tadka needs a primary technology stack for its backend API. The platform will start as a monolith (see ADR-002) and eventually extract services. The tech choice affects hiring, performance, ecosystem, and how well AI-assisted development (GitHub Copilot) works with the codebase.

Our team of 6 has mixed backgrounds: 3 from .NET shops (Infosys, TCS enterprise projects), 2 from Java/Spring Boot (one ex-Flipkart), 1 from Node.js. We're hiring in the Bangalore market where Java dominates but .NET is growing fast, especially in startups and fintech.

We need:
- High performance for real-time order processing (target P99 < 300ms for order placement)
- Strong typing and domain modeling support (DDD with aggregates, value objects)
- Excellent tooling for a monolith-first approach that evolves into services
- Good GitHub Copilot support (code generation quality matters when a team of 6 needs to ship fast)

## Decision

Use .NET 10 with ASP.NET Core (Controllers pattern) as the primary backend framework. Use C# as the language. Use EF Core with Npgsql as the ORM for PostgreSQL.

## Consequences

### Positive

- **Performance:** .NET 10 ranks consistently in the top 5 on TechEmpower benchmarks for both plaintext and JSON serialization. Minimal API and Controller pipelines handle 1M+ req/sec on commodity hardware. For Tadka's scale (100-1000 req/sec initially), this is massive headroom.
- **Language features for DDD:** C# records for value objects (`record Money(decimal Amount, string Currency)`), pattern matching for state transitions, nullable reference types for compile-time null safety. These features make domain modeling clean without boilerplate.
- **Single ecosystem from monolith to microservices:** The same framework works for a single API project and for 5 extracted services. No framework switch when we evolve. MediatR, EF Core, Polly, YARP all compose naturally.
- **GitHub Copilot quality:** C# has excellent Copilot support. The type system gives Copilot strong context, and .NET conventions are well-represented in training data. Our team saw noticeably better suggestions compared to Go or Rust.
- **Cross-platform:** Runs on Linux containers (Docker/ECS) with no Windows dependency. Same codebase works on devs' Windows, macOS, or Linux machines.
- **Mature ecosystem:** Built-in dependency injection, configuration, logging, health checks, OpenAPI. Less glue code than Spring Boot for the same functionality.

### Negative

- **Smaller talent pool in India compared to Java:** Job postings on Naukri show 3-4x more Java roles than .NET roles. Hiring senior .NET engineers in Bangalore is harder than hiring Java engineers.
- **Fewer Indian tech blogs and tutorials:** Most system design content in India uses Java/Spring Boot examples. Our engineers will reference English-language Microsoft docs more than Hinglish blog posts.
- **Enterprise reputation:** .NET still carries a "Windows enterprise / IT company" stigma in some Indian startup circles. Some candidates may dismiss a .NET role without investigating further.
- **EF Core vs raw SQL:** EF Core generates good SQL for most queries but can produce suboptimal joins for complex reporting. We'll need to drop to raw SQL or Dapper for analytics queries.

### Risks

- **Risk:** Hiring pipeline dries up for .NET engineers as we scale past 15 engineers. **Mitigation:** .NET is growing in Indian fintech (Razorpay's internal tools, some CRED backend services). The strong type system means engineers from Java/Kotlin backgrounds ramp up in 2-3 weeks. We'll invest in onboarding docs.
- **Risk:** Team defaults to enterprise-style over-engineering (repository pattern wrapping EF Core, excessive abstraction layers). **Mitigation:** Code reviews enforce simplicity. No repository pattern, EF Core DbContext is the repository. Keep layers minimal.
- **Risk:** .NET 10 is the latest version. Libraries may have compatibility gaps. **Mitigation:** All critical dependencies (EF Core, Npgsql, MediatR, Polly, Serilog) already support .NET 10. We verified before this decision.

## Alternatives Considered

### Option A: Java 21 + Spring Boot 3.x
- Pros: Largest talent pool in India by far. Massive ecosystem (Spring Cloud, Spring Security). Every system design tutorial uses Java examples. Battle-tested at Flipkart, PhonePe, Swiggy scale.
- Cons: More verbose for domain modeling (no records until Java 16, still not as ergonomic as C#). Spring Boot startup time is slower (matters for local dev iteration). XML/annotation configuration overhead. Copilot suggestions often generate boilerplate-heavy patterns.
- Why rejected: The verbosity tax adds up across an 8-week project. C#'s records, pattern matching, and minimal ceremony for DDD outweigh Java's larger ecosystem. Our team's existing .NET experience tips the balance.

### Option B: Go
- Pros: Excellent performance, tiny binaries, fast compilation, great for microservices. Growing in Indian startups (Zerodha backend is Go).
- Cons: No generics until recently (still limited). No ORM as mature as EF Core. Domain modeling is painful without classes/inheritance. Error handling is verbose. Copilot suggestions are weaker due to less training data variety.
- Why rejected: Go is great for individual microservices but painful for a monolith with rich domain logic. Tadka's order lifecycle, payment state machines, and delivery assignment algorithms need expressive domain modeling that Go doesn't support well.

### Option C: Node.js (TypeScript) + Express/Fastify
- Pros: Huge developer pool, fast prototyping, shared language with frontend, excellent Copilot support.
- Cons: Single-threaded event loop struggles with CPU-bound operations (route optimization, pricing calculations). TypeScript's type system is structural, not nominal, weaker for DDD. Runtime type safety requires extra libraries (Zod, io-ts). ORM ecosystem is fragmented (Prisma vs TypeORM vs Drizzle, none as mature as EF Core).
- Why rejected: Fine for CRUD APIs but Tadka's domain complexity (order state machines, delivery assignment algorithms, payment reconciliation) benefits from a stronger runtime type system. Performance ceiling is lower for compute-heavy operations.

## References
- [TechEmpower Framework Benchmarks Round 22](https://www.techempower.com/benchmarks/) (.NET in top 5 for JSON, DB, Fortune)
- [C# 12/13 Language Features](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/) (records, pattern matching, primary constructors)
- [.NET Adoption in Indian Startups](https://www.linkedin.com/pulse/dotnet-india-startup-ecosystem/) (growing in fintech, healthtech)
- ADR-002: Why we start as a monolith (architecture decision that .NET supports well)
