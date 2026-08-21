# ADR-008: No Cross-Schema Foreign Keys

**Date:** 2026-05-30
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Tadka uses schema-per-domain (ADR-003) with 5 schemas in a single PostgreSQL database. Many entities reference entities in other schemas: `ordering.orders.customer_id` points to a user in the `identity` schema, `ordering.orders.restaurant_id` points to a restaurant in the `restaurant` schema, `delivery.assignments.order_id` points to an order in the `ordering` schema.

The question is whether to enforce these cross-domain references with database foreign keys.

## Decision

Foreign keys are allowed only within a schema. No cross-schema foreign keys. Cross-schema references use application-level validation.

**Within schema (FK enforced):**
- `ordering.order_items.order_id` → `ordering.orders.id` ✅
- `restaurant.menu_items.restaurant_id` → `restaurant.restaurants.id` ✅
- `delivery.assignments.agent_id` → `delivery.agents.id` ✅

**Across schemas (application-level only):**
- `ordering.orders.customer_id` → (validated in code, not FK) ✅
- `ordering.orders.restaurant_id` → (validated in code, not FK) ✅
- `delivery.assignments.order_id` → (validated in code, not FK) ✅

## Consequences

### Positive

- **Clean service extraction.** When we extract the ordering service to its own database, there are no foreign keys to break. The migration is: dump schema, load into new database, done. Cross-schema FKs would require coordination between databases that are being separated.
- **Independent schema evolution.** Dropping and recreating the `identity.users` table (say, during a major auth refactor) doesn't cascade-fail orders. Each schema evolves independently.
- **No cascade surprises.** Deleting a user doesn't CASCADE DELETE their orders, or worse, fail with a FK constraint violation that blocks user management.
- **Matches the service boundary model.** In a microservices world, services can't have database-level FKs to other services. We practice that discipline now while it's cheap.

### Negative

- **Orphan data is possible.** You can insert an order with `customer_id = "nonexistent-uuid"` if the application code has a bug. The database won't catch it.
- **No database-level referential integrity across domains.** A DBA looking at the schema sees dangling references with no guarantees. This feels wrong to anyone trained on normalized relational design.

### Risks

- **Risk:** Application-level validation has a bug, orphan records accumulate. **Mitigation:** Integration tests cover all cross-domain operations. A weekly consistency check query (simple LEFT JOIN looking for orphans) catches any gaps. At Tadka's scale, this is a 5-second query.
- **Risk:** Developers forget to validate cross-domain references in new code. **Mitigation:** Service layer methods for cross-domain lookups (`GetRestaurantOrThrow(restaurantId)`) make the validation explicit and hard to skip. Code reviews catch missing checks.

## Alternatives Considered

### Option A: Foreign Keys Everywhere (Full Referential Integrity)
- Pros: Database guarantees consistency. No orphan records possible. A DBA's dream schema.
- Cons: Cross-schema FKs create hard dependencies. Can't delete a user without handling all related orders (CASCADE or SET NULL). Can't extract a schema to a separate database without breaking FKs. Schema migrations become coupled: changing `identity.users.id` type requires migrating every table that references it.
- Why rejected: Trades extraction-readiness for database-level consistency we can enforce in application code. The monolith-to-services migration path matters more than catching a rare orphan record.

### Option B: No Foreign Keys at All
- Pros: Maximum flexibility. No constraints anywhere. Schema changes are trivial.
- Cons: Even within-schema references have no integrity. `order_items` could reference a nonexistent `orders.id`. This is too permissive. Within a domain, the entities are tightly coupled and will always live together.
- Why rejected: Within-schema FKs are safe and useful. `order_items` will always live with `orders` even after service extraction. No reason to give up that integrity.

## References

- ADR-003: Schema-Per-Domain (this decision enforces the boundaries set up there)
- ADR-002: Start with a Monolith (service extraction readiness is the motivation)
- [Microservices Patterns by Chris Richardson](https://microservices.io/patterns/data/database-per-service.html) (Database per Service pattern, which this prepares for)

## Revisit When

When load testing or business requirements prove this decision is a bottleneck, or when specific pain points mentioned in 'Risks' are realized.
