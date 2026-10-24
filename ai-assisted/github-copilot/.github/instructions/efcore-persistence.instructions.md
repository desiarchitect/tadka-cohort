---
name: EF Core persistence
description: Entity configuration, schemas, value objects and concurrency in Tadka. Use when editing entities, DbContexts or their configurations.
applyTo: "src/**/Data/**/*.cs"
---

# EF Core rules

1. **Fluent API only.** One `IEntityTypeConfiguration<T>` per entity, in a `Configurations/` folder. No `[Table]`, `[Column]` or `[Required]` on domain classes.
2. **Name the schema.** `builder.ToTable("users", "identity")`. Never configure `HasOne` or `HasMany` toward an entity in another schema (ADR-008).
3. **Value objects are owned types.** `builder.OwnsOne(o => o.TotalAmount, money => { ... })`. Money, Address and GeoLocation are C# records.
4. **Optimistic concurrency uses `xmin`** (ADR-012):
   ```csharp
   builder.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
   ```
   A lost race returns 409.
5. **Indexes** for the ids you filter by, for example `(customer_id, created_at DESC)` (ADR-014).
6. **Read-only queries** use `AsNoTracking()`. Reads that may lag go to the replica through `TadkaReadDbContext`; reads right after a write stay on the primary (ADR-016).
7. **Migrations are per service.** Each service has its own `Migrations/` folder and context. Never edit a migration that is already applied.
