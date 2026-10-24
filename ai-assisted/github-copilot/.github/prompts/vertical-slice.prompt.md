---
name: vertical-slice
description: Add one endpoint end to end (request and response types, controller action, persistence, test) by copying an existing one
agent: agent
---

Add this endpoint: ${input:endpoint:for example "GET /api/v1/restaurants/{id}/operating-hours"}

Work like this:

1. **Pick the owner.** Orders and Identity live in `src/Tadka.Api`. Restaurant, Payment and Delivery are their own projects. Put the endpoint in the service that owns the data. Never add a call between services for it.
2. **Copy a neighbour.** Open the closest existing endpoint in that project (for Restaurant, `RestaurantsController.cs`) and mirror its route style, status codes, auth attributes and error handling. The rules are in [the controller instructions](../instructions/api-controllers.instructions.md).
3. **Contract first.** Show me the route, request, response and error cases before you write code, and wait for my OK.
4. **Then build:** request and response records, the controller action, the entity and its Fluent configuration if data changes, and a migration for that service only.
5. **Prove it.** Add an integration test with the service's existing `*ApiFactory`, then run `dotnet build Tadka.slnx` and the project's tests. Show the output.

Keep every `// (ADR-NNN)` comment. Do not add NuGet packages.
