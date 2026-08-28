# ADR-004: EF Core Code-First as ORM

**Date:** 2026-05-30
**Status:** Accepted
**Deciders:** Tadka Engineering Team

**Topic:** How do we persist the Day 2 C# domain model to Postgres?

**Options:**
1. EF Core code-first (classes generate schema; `OwnsOne` for value objects).
2. EF Core database-first (schema generates classes).
3. Dapper / hand-written SQL.
4. Raw ADO.NET.

**Choice:** Option 1. Npgsql, migrations in git, `OwnsOne` for Money and Address.

**Why:** The domain already exists in C#. Code-first keeps one source of truth. `OwnsOne` maps snapshots without extra tables. Teaching scale (then ~1 lakh orders/day) is inside EF's ceiling. Dapper waits for a measured read path.

**Trade-off:** Generated SQL can be 10–15% slower; N+1 is a real foot-gun. Complex reporting fights LINQ translation.

**Failure mode:** Nobody looks at the SQL, an unindexed query ships, pool holds connections (Day 5). Or 100k seed rows stuffed into `HasData()` and every deploy re-checks them.

**Revisit when:** Day 5 query plans show a hot path EF cannot express well — then Dapper or raw SQL for *that* read, not a wholesale ORM swap.
