# ADR-007: Two-Layer Validation (FluentValidation + Domain Invariants)

**Date:** 2026-05-30
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Tadka needs a validation strategy for incoming requests and domain operations. Validation happens at two distinct levels: "is this input well-formed?" (API boundary) and "does this operation make business sense?" (domain layer). Conflating the two leads to either leaky abstractions or missed validation paths.

A critical concern: when we add Kafka consumers (Day 9) and background jobs (Day 14), those callers bypass the API layer entirely. If validation lives only in controllers, bad data enters through the side door.

## Decision

Use two layers of validation:

1. **FluentValidation at the API boundary.** Validates request DTOs before any business logic runs. Catches shape errors: required fields, format validation (email, phone), range checks (quantity > 0), array constraints (items must have at least one element). Returns 400 Bad Request.

2. **Domain entity invariants in the entity classes.** Validates business rules inside domain logic. Catches semantic errors: "can this order be cancelled in its current state?", "is this state transition valid?", "does this restaurant have active menu items?". Returns 422 Unprocessable Entity for business rule violations.

## Consequences

### Positive

- **Separation of concerns.** API layer validates shape, domain layer validates meaning. Each layer does one job.
- **Side-door protection.** Internal callers (Kafka consumers, background jobs, CLI tools) go through domain validation even if they bypass the API layer. Business rules are never skipped.
- **Fast rejection.** FluentValidation at the boundary rejects obviously bad requests (missing required fields, invalid email format) without touching the database. No wasted DB round trips.
- **Clear error semantics.** 400 = your request is malformed, fix the input. 422 = your request is well-formed but violates a business rule. Frontend developers know exactly what to show the user.
- **Testable independently.** FluentValidation rules have unit tests. Domain invariants have unit tests. Neither depends on the other.

### Negative

- **Two validation layers to maintain.** Renaming a field means updating the DTO validator and potentially the domain invariant. More surface area for bugs.
- **Validation logic can appear duplicated.** Both layers might check "quantity > 0", but for different reasons: API layer checks input shape, domain layer enforces the invariant. Looks like duplication, isn't.

### Risks

- **Risk:** Developers validate only at the API layer because it's easier, skip domain invariants. Then Kafka consumer sends invalid data straight into the database. **Mitigation:** Domain entity constructors and methods enforce invariants. You can't create an `Order` with zero items because the constructor throws. The entity protects itself.
- **Risk:** Error messages from two layers confuse frontend developers. **Mitigation:** Standardize on RFC 7807 (ADR-006) for both layers. API validation errors use status 400, domain errors use 422. Same `errors` array shape.

## Alternatives Considered

### Option A: Data Annotations Only
- Pros: Built into ASP.NET Core. `[Required]`, `[Range(1, 100)]`, `[EmailAddress]` on DTO properties. Zero configuration.
- Cons: Limited expressiveness. "Items array must have at least one element with a valid menuItemId" is impossible with attributes alone. No conditional validation. No cross-field validation. Can't express: "if paymentMethod is UPI, then upiId is required."
- Why rejected: Too limited for Tadka's validation needs. FluentValidation handles everything Data Annotations can, plus complex rules.

### Option B: Domain Entity Validation Only
- Pros: Single source of truth. All validation lives in the domain. No duplication.
- Cons: Wastes resources. A request with a missing required field still hits the service layer, loads entities from DB, and then fails validation. For a request with an invalid email format, you've done a database round trip for nothing. And error messages from deep domain layer are harder to map to specific input fields for the frontend.
- Why rejected: Wasteful and poor user experience. API boundary should reject bad input immediately.

### Option C: Manual Validation in Controllers
- Pros: Full control. Write `if (request.Items == null) return BadRequest(...)` directly.
- Cons: Validation logic scattered across controller methods. No reuse. No testability. Every controller has a wall of `if` statements before the actual logic. Adding a new field means finding every controller that uses the DTO and adding the check.
- Why rejected: Doesn't scale. With 20+ endpoints, manual checks become a maintenance nightmare.

## References

- ADR-006: RFC 7807 Problem Details (error format for both validation layers)
- [FluentValidation Documentation](https://docs.fluentvalidation.net/)
- [Domain-Driven Design: Tackling Complexity in the Heart of Software](https://www.domainlanguage.com/ddd/) (Chapter on entity invariants)

## Revisit When

When load testing or business requirements prove this decision is a bottleneck, or when specific pain points mentioned in 'Risks' are realized.
