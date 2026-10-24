---
name: API controllers
description: How controllers, routes, status codes and error bodies are written in Tadka. Use when adding or changing an HTTP endpoint.
applyTo: "src/Tadka.Api/Controllers/**/*.cs,src/Tadka.Restaurant.Api/Controllers/**/*.cs"
---

# Controller standards

Payment and Delivery use minimal endpoints in `Program.cs`. For those, copy the style already in that file instead.

1. Inherit `ControllerBase`, add `[ApiController]`, and write the **full route**: `[Route("api/v1/orders")]`. Do not use `[controller]` tokens. Versioning is in the URL (ADR-010).
2. Return `ActionResult<T>`. Use `CreatedAtAction` for a 201.
3. Status codes:
   - 400 malformed input
   - 401 or 403 auth
   - 404 not found
   - 422 valid request, but a business rule said no
   - 409 you lost a race (concurrency)
4. Errors are RFC 7807 Problem Details (ADR-006). Call `Problem(detail: ..., statusCode: ..., title: ...)`. Never return raw exceptions or stack traces.
5. Secure every non-public action with `[Authorize]`. Check ownership in the action, for example `if (!User.IsAdmin() && order.CustomerId != User.UserId()) return Forbid();`: a customer reads only their own orders.
6. Services that use `[Authorize(Roles = ...)]` must have `MapInboundClaims = false`, or the `role` claim is invisible.
7. Orders, menus and restaurants have no `PUT` and no `DELETE`: use `PATCH` for partial updates and a state change (cancel, deactivate) instead of deleting, so history is never lost. (A few small endpoints, such as feature flags and rider location, do use `PUT`.) Never accept a price from the client.
