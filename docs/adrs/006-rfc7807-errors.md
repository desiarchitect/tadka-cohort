# ADR-006: RFC 7807 Problem Details for Error Responses

**Date:** 2026-05-30
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Tadka's API needs a consistent error response format. Every endpoint must return errors in a shape that frontend developers, mobile developers, and third-party consumers can parse without guessing. Inconsistent error formats across endpoints create integration friction and debugging nightmares.

ASP.NET Core has built-in support for RFC 7807 Problem Details via the `ProblemDetails` class. We need to decide whether to use this standard or roll our own.

## Decision

Use RFC 7807 Problem Details as the standard error response format for all API endpoints. Use ASP.NET Core's built-in `ProblemDetails` and `ValidationProblemDetails` classes. Field-level validation errors use `ValidationProblemDetails`, whose `errors` is an **object map** (field → array of messages) — the ASP.NET Core / RFC 7807 standard shape, not a custom array.

**Standard validation error shape** (what the middleware actually returns; matches `docs/api-specs/error-standard.md`):
```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
  "title": "Validation Failed",
  "status": 400,
  "detail": "One or more validation errors occurred.",
  "errors": {
    "Items": ["Items must not be empty."],
    "DeliveryAddress.Pincode": ["Pincode must be exactly 6 digits."]
  }
}
```

> **Status codes:** malformed/validation failures are **400**. A syntactically valid request that breaks a domain rule (illegal state transition, ordering an unavailable item) is **422** — a `ProblemDetails` with `title`/`detail` but no `errors` map.

## Consequences

### Positive

- **Industry standard.** RFC 7807 is an IETF standard. Client libraries in every language know how to parse it. No documentation needed for the error shape itself.
- **Machine-parseable.** The `type` field is a URI that uniquely identifies the error category. Clients can switch on `type` for programmatic error handling without parsing human-readable strings.
- **ASP.NET Core native support.** `ProblemDetails` middleware handles 404, 500, and unhandled exceptions automatically. No custom middleware needed for the basics.
- **Self-documenting.** The `title` field is a human-readable summary. The `detail` field has specifics. The `instance` field shows which request caused it. Debugging from a log is straightforward.
- **Extensible.** The `errors` array for field-level validation details is a standard extension point. No schema violation.

### Negative

- **More verbose than minimal formats.** `{error: "Not found"}` is 20 bytes. A ProblemDetails 404 is 150+ bytes. For high-volume APIs, this adds up (negligibly, but it's there).
- **Type URIs need maintenance.** The `type` field should be a resolvable URI that describes the error. In practice, teams often use placeholder URIs that never resolve. Requires discipline.

### Risks

- **Risk:** Developers return raw exceptions or custom JSON shapes in some endpoints, breaking consistency. **Mitigation:** Global exception handler middleware converts all unhandled exceptions to ProblemDetails. Code reviews catch endpoints that bypass the standard.
- **Risk:** The `errors` array format differs between endpoints. **Mitigation:** Centralized `ApiError` record with `field` and `message` properties. All validation errors use this shape.

## Alternatives Considered

### Option A: Custom JSON Format
- Example: `{success: false, message: "Not found", code: "ORDER_NOT_FOUND"}`
- Pros: Simple, lightweight, easy to implement.
- Cons: Every team invents their own shape. The mobile developer spends a week figuring out your error format. No tooling support. No standard for validation errors vs business errors vs server errors.
- Why rejected: Custom means proprietary. Every new consumer learns a new format. RFC 7807 is the "one format to rule them all."

### Option B: Custom Numeric Error Codes
- Example: `{code: 40401, message: "Order not found"}`
- Pros: Machine-friendly, compact, easy to switch on.
- Cons: Requires a lookup table. Error 40401 vs 40402 vs 40403, what do they mean? Documentation overhead grows linearly with error codes. No standard for nesting validation details.
- Why rejected: Numeric codes are 1990s-era. URIs (RFC 7807's `type` field) are self-documenting and linkable.

### Option C: GraphQL-Style Errors Array
- Example: `{errors: [{message: "...", locations: [...], path: [...]}]}`
- Pros: Good for GraphQL APIs. Rich error context with path information.
- Cons: Only makes sense in a GraphQL context. For REST, it ignores HTTP status codes entirely. Not an HTTP standard.
- Why rejected: We're building REST (ADR-005). Use the REST error standard.

## References

- [RFC 7807: Problem Details for HTTP APIs](https://www.rfc-editor.org/rfc/rfc7807)
- [ASP.NET Core Problem Details](https://learn.microsoft.com/en-us/aspnet/core/web-api/handle-errors)
- Tadka Error Standard: `docs/api-specs/error-standard.md`

## Revisit When

When load testing or business requirements prove this decision is a bottleneck, or when specific pain points mentioned in 'Risks' are realized.
