# ADR-003: Schema-Per-Domain in a Single Database

**Date:** 2026-05-30
**Status:** Accepted
**Deciders:** Tadka Engineering Team

**Topic:** Five bounded contexts, one Postgres. How do we organize tables so ownership is visible and extraction later is a dump, not a shuffle?

**Options:**
1. All tables in `public`.
2. One schema per bounded context, same database (`ordering`, `restaurant`, `delivery`, `identity`, `payment`).
3. One database per context from day one.

**Choice:** Option 2. Qualify tables (`ordering.orders`). EF `ToTable("orders", "ordering")`. Cross-schema FKs are banned separately (ADR-008).

**Why:** Shared `public` invites "just add a foreign key" across domains. Separate databases on day one buy distributed transactions and no JOINs for a monolith that does not need them. Schema-per-domain is logical isolation with one pool, one backup, one `\dn`.

**Trade-off:** Every entity needs an explicit schema in EF. First migration must `CREATE SCHEMA`. Slightly more ceremony than `public`.

**Failure mode:** People treat schemas as folders and JOIN across them in app SQL, or add a sixth schema per feature. Extraction then still requires surgery.

**Revisit when:** A context is extracted to its own process (that schema becomes that service's database). Not when someone wants a prettier `\dt`.
