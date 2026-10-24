---
description: Run the architecture boundary tests, then look for cross-schema foreign keys and illegal service references by hand
allowed-tools: Bash(dotnet test *) Read Grep Glob
---

Check that the architecture boundaries still hold.

## Step 1: run the automated tests

Run this and show the result:

```
dotnet test tests/Tadka.Api.Tests --filter "FullyQualifiedName~BoundaryTests" --nologo
```

These tests guard ADR-008 (no foreign key crosses a schema) and ADR-022 and ADR-024 (Orders never references Payment, and the monolith does not own the Restaurant aggregate).

## Step 2: look for what the tests cannot see

Use Grep and Read to check:

1. **Foreign keys across schemas (ADR-008).** In every `IEntityTypeConfiguration` under `src/**/Data/`, look for `HasOne`, `HasMany` or `HasForeignKey` that points at an entity from another schema. Cross-domain links must be plain `Guid` ids.
2. **Orders to Payment (ADR-022, ADR-024).** Under `src/Tadka.Api/`, look for `using Tadka.Payment` or any call that reaches the Payment database. Orders and Payment talk only through Kafka topics.
3. **Price lookups (ADR-037).** Order pricing must read `IRestaurantPricingSource` (the local `menu_replica`). Flag any HTTP call to the Restaurant service on the order-placement path.
4. **Events without a trace (ADR-041).** A new producer or consumer that does not carry `traceparent` makes the saga fall off the trace.

## Report

If the tests pass and the manual check is clean, say so in two lines. If not, give the file, the line, what is wrong, and the ADR it breaks.
