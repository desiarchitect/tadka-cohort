# Cross-Stack Technology Mapping

The architectural patterns taught in the Desi Architect cohort (Transactional Outbox, Saga Choreography, Idempotency, Circuit Breaking, etc.) are **language-agnostic**. However, the reference implementation (`Tadka`) is written in .NET 10. 

If you are coming from a Java or Node.js background, this document serves as your "Rosetta Stone" to map the .NET libraries used in the codebase to the industry-standard equivalents in your stack.

---

## 1. Core Framework & Hosting

The foundation of the microservices.

| Concept | .NET (Tadka Implementation) | Java Ecosystem | Node.js Ecosystem |
| :--- | :--- | :--- | :--- |
| **Web Framework** | ASP.NET Core Minimal APIs | Spring Boot 3+ (Spring Web) | NestJS / Express |
| **Dependency Injection** | Built-in `IServiceCollection` | Spring IoC Container | NestJS DI / InversifyJS |
| **Configuration** | `appsettings.json` / `IOptions<T>` | `application.yml` / `@Value` | `dotenv` / NestJS `@nestjs/config` |

## 2. Data Access & Persistence

How services talk to PostgreSQL.

| Concept | .NET (Tadka Implementation) | Java Ecosystem | Node.js Ecosystem |
| :--- | :--- | :--- | :--- |
| **ORM** | Entity Framework Core (EF Core) | Hibernate / Spring Data JPA | Prisma / TypeORM / Sequelize |
| **Database Migrations** | EF Core Code-First Migrations | Flyway / Liquibase | Prisma Migrate / Knex Migrations |
| **Read/Write Split** | `AddDbContext` (Primary) + `AddDbContext` (Replica w/ NoTracking) | Spring `@Transactional(readOnly = true)` routing | Prisma Client Extensions / TypeORM replication |
| **Pessimistic Locking** | Raw SQL: `SELECT ... FOR UPDATE` | `@Lock(LockModeType.PESSIMISTIC_WRITE)` | Prisma `$executeRaw` / TypeORM `setLock` |

## 3. Asynchronous Messaging & Architecture

How services communicate and decouple logic.

| Concept | .NET (Tadka Implementation) | Java Ecosystem | Node.js Ecosystem |
| :--- | :--- | :--- | :--- |
| **In-Process Messaging (CQRS)** | MediatR | Spring ApplicationEvents / Axon Framework | NestJS CQS Module / Node `EventEmitter` |
| **Message Broker (Kafka)** | Confluent.Kafka (.NET Client) | Spring Kafka / Apache Kafka Java Client | KafkaJS |
| **Background Workers / Daemons** | `IHostedService` / `BackgroundService` | Spring `@Scheduled` / Spring TaskExecutor | BullMQ / PM2 / node-cron |
| **API Gateway** | YARP (Yet Another Reverse Proxy) | Spring Cloud Gateway | Express Gateway / Fastify-reply-from / Kong |

## 4. Resilience & Scaling

How the system protects itself from cascading failures.

| Concept | .NET (Tadka Implementation) | Java Ecosystem | Node.js Ecosystem |
| :--- | :--- | :--- | :--- |
| **Circuit Breakers & Retries** | Polly | Resilience4j | Opossum / Cockatiel |
| **Distributed Caching** | `IDistributedCache` (StackExchange.Redis) | Spring Cache (`RedisTemplate`) | `ioredis` / `node-redis` |
| **Rate Limiting** | ASP.NET Core RateLimiting Middleware | Bucket4j / Spring Cloud Gateway filters | `express-rate-limit` / NestJS Throttler |
| **Load Shedding / Backpressure** | Custom Semaphore Middleware (ADR-059) | Tomcat max-threads / Spring WebFlux | Node generic pool / `express-status-monitor` |

## 5. Security & Identity

How the system identifies users and protects data.

| Concept | .NET (Tadka Implementation) | Java Ecosystem | Node.js Ecosystem |
| :--- | :--- | :--- | :--- |
| **Authentication (JWT)** | `AddJwtBearer` | Spring Security (OAuth2 Resource Server) | Passport.js (JWT Strategy) |
| **Authorization (RBAC)** | `[Authorize(Roles = "...")]` | `@PreAuthorize("hasRole(...)")` | NestJS Guards / Express middleware |
| **Password Hashing** | ASP.NET Core Identity `PasswordHasher` | Spring Security `BCryptPasswordEncoder` | `bcrypt` / `argon2` npm packages |
| **PII Encryption at Rest** | Custom `ValueConverter` (ADR-052) | Hibernate `@ColumnTransformer` / Jasypt | Prisma Client Extensions / `mongoose-encryption` |

## 6. Observability & Testing

How we monitor production and verify correctness.

| Concept | .NET (Tadka Implementation) | Java Ecosystem | Node.js Ecosystem |
| :--- | :--- | :--- | :--- |
| **Distributed Tracing** | OpenTelemetry .NET / `ActivitySource` | OpenTelemetry Java / Spring Cloud Sleuth (Micrometer) | OpenTelemetry JS / Node tracer |
| **Unit/Integration Testing** | xUnit | JUnit 5 | Jest / Mocha / Vitest |
| **Integration Dependencies** | Testcontainers for .NET | Testcontainers (Java) | Testcontainers for Node.js |
| **Live Tracking (SSE)** | ASP.NET Core Response Streams / Channels | Spring WebFlux Server-Sent Events | Express `res.write` / NestJS SSE |

---

### How to use this guide

When the curriculum references a .NET specific file (e.g., "Look at how `OutboxRelay.cs` inherits from `BackgroundService` to poll the database"), you should mentally substitute:
- **Java:** "I would use a `@Scheduled` method in Spring Boot to poll the database."
- **Node.js:** "I would use a BullMQ worker or `setInterval` daemon to poll the database."

The logic (the SQL queries, the Kafka commits, the idempotency checks) remains identical across all stacks.
