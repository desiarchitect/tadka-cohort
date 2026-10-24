---
name: ef-migration-generator
description: Add an EF Core migration to the right Tadka service and check it for schema-per-domain problems. Use when an entity or its configuration changes in Orders, Payment, Delivery or Restaurant.
allowed-tools: Read Grep Glob
---

# EF Core migration for Tadka

Each service owns its database and its own migration history. A migration for the wrong service is the most common mistake, so the helper script takes the **service** and picks the project and context for you.

| Service | Project | DbContext |
|---|---|---|
| Orders | `src/Tadka.Api` | `TadkaDbContext` |
| Payment | `src/Tadka.Payment.Api` | `PaymentDbContext` |
| Delivery | `src/Tadka.Delivery.Api` | `DeliveryDbContext` |
| Restaurant | `src/Tadka.Restaurant.Api` | `RestaurantDbContext` |

## Before you generate

1. Name the service that owns the entity. If you are unsure, stop and ask.
2. Check the configuration uses Fluent API in an `IEntityTypeConfiguration<T>` and names the schema: `ToTable("orders", "ordering")`.
3. Check **no foreign key or navigation property points at another schema** (ADR-008). Cross-domain links are plain `Guid` ids.
4. If the change drops or renames a column in a live system, use expand and contract (ADR-038): add the new column first, backfill in chunks, remove the old one in a later release.

## Generate

The tool `dotnet-ef` must be installed (`dotnet tool install --global dotnet-ef`). Then:

```powershell
powershell -File ${CLAUDE_SKILL_DIR}/scripts/add-migration.ps1 -Service Restaurant -MigrationName AddOperatingHours
```

Add `-WhatIf` to see the command without running it.

## After you generate

1. Open the new file in the service's `Migrations/` folder and read it. Check the schema name, table names, column types and indexes. The `xmin` column must not appear as a normal column.
2. Check the `Down` method reverses `Up`.
3. Run `dotnet build Tadka.slnx`, then the service's tests: `dotnet test tests/Tadka.<Service>.Tests`.
4. Tell the user which files changed. Do not apply the migration to a database unless asked.
