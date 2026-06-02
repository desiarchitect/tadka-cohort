# Error Standard: RFC 7807 Problem Details

All Tadka API errors use [RFC 7807 Problem Details](https://www.rfc-editor.org/rfc/rfc7807) format. Content-Type is `application/problem+json`.

This gives every error a consistent, machine-readable shape. No more guessing whether the error body has `message`, `error`, `errors`, or some other field name.

---

## Error Response Shape

```json
{
  "type": "https://tools.ietf.org/html/rfc7807",
  "title": "Short human-readable summary",
  "status": 400,
  "detail": "Longer explanation of what went wrong",
  "traceId": "00-abc123...-00"
}
```

| Field | Required | Description |
|-------|----------|-------------|
| type | yes | URI identifying the error type |
| title | yes | Short summary (same for all instances of this error type) |
| status | yes | HTTP status code |
| detail | no | Human-readable explanation specific to this occurrence |
| traceId | no | Request correlation ID for debugging |

ASP.NET Core generates this format automatically via `ProblemDetails`.

---

## Error Categories

### 400 Bad Request — Validation Errors

Triggered by FluentValidation when request body fails validation rules.

```json
{
  "type": "https://tools.ietf.org/html/rfc7807",
  "title": "Validation Failed",
  "status": 400,
  "detail": "One or more validation errors occurred.",
  "errors": {
    "Items": [
      "Items must not be empty."
    ],
    "DeliveryAddress.Pincode": [
      "Pincode must be exactly 6 digits."
    ]
  }
}
```

The `errors` object maps field names (in JSON path format) to an array of error messages. A single field can have multiple validation failures.

---

### 404 Not Found

Triggered when a requested resource doesn't exist. Uses custom `NotFoundException` caught by middleware.

```json
{
  "type": "https://tools.ietf.org/html/rfc7807",
  "title": "Not Found",
  "status": 404,
  "detail": "Restaurant with id 'a1b2c3d4-9999-4000-8000-000000000001' was not found."
}
```

---

### 422 Unprocessable Entity — Domain Rule Violation

Triggered when the request is syntactically valid but violates a business rule. Uses custom `DomainException` caught by middleware.

**Example: Invalid order status transition**

```json
{
  "type": "https://tools.ietf.org/html/rfc7807",
  "title": "Domain Rule Violation",
  "status": 422,
  "detail": "Cannot transition order from 'Delivered' to 'Confirmed'. Order is already in a terminal state."
}
```

**Example: Cancel a non-cancellable order**

```json
{
  "type": "https://tools.ietf.org/html/rfc7807",
  "title": "Domain Rule Violation",
  "status": 422,
  "detail": "Cannot cancel order in status 'Delivered'. Only orders in 'Created' or 'Confirmed' status can be cancelled."
}
```

---

### 409 Conflict — Concurrent Update

Triggered when an order was modified by another request between your read and your write — an
optimistic-concurrency conflict (ADR-012). The request was **legal**; you simply lost a race.
This is distinct from `422`: a `422` means *the request itself broke a rule*; a `409` means
*you raced someone and lost — reload and retry*. Detected via EF Core's `DbUpdateConcurrencyException`.

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Concurrent Update Conflict",
  "status": 409,
  "detail": "This order was modified by another request. Reload the order and try again."
}
```

> **Client guidance:** on `409`, re-fetch the order, re-evaluate, and retry if the action still applies.

---

### 500 Internal Server Error

Unhandled exceptions. Never exposes stack traces or implementation details to the client.

```json
{
  "type": "https://tools.ietf.org/html/rfc7807",
  "title": "Internal Server Error",
  "status": 500,
  "detail": "An unexpected error occurred. Please try again later."
}
```

The actual exception is logged server-side with full stack trace. The client only sees the safe message above.

---

## Implementation

The `ExceptionHandlingMiddleware` in `Tadka.Api/Middleware/ExceptionHandlingMiddleware.cs` catches exceptions and maps them:

| Exception Type | HTTP Status | Title |
|---------------|-------------|-------|
| `FluentValidation.ValidationException` | 400 | Validation Failed |
| `NotFoundException` | 404 | Not Found |
| `DomainException` | 422 | Domain Rule Violation |
| `DbUpdateConcurrencyException` | 409 | Concurrent Update Conflict |
| Everything else | 500 | Internal Server Error |

```
Client Request
     ↓
ExceptionHandlingMiddleware (try/catch)
     ↓
Controller / Business Logic
     ↓ (throws)
ExceptionHandlingMiddleware (catch → ProblemDetails response)
     ↓
Client receives RFC 7807 JSON
```

---

## Why RFC 7807?

Before standardized error responses, every team invented their own format:

```json
// Team A
{ "error": "not found" }

// Team B  
{ "message": "Not Found", "code": 404 }

// Team C
{ "errors": [{ "msg": "not found", "field": null }] }
```

Frontend developers had to handle all three. RFC 7807 gives one format that every endpoint returns. ASP.NET Core supports it out of the box.
