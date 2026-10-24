---
name: Architecture boundaries
description: Service boundaries, schema isolation, events and read models in Tadka. Use when changing anything that crosses a domain or service.
applyTo: "src/**/*.cs"
---

# Architecture rules

1. **Respect the boundaries.** Orders (`src/Tadka.Api`), Payment, Delivery and Restaurant are separate services with separate databases. Do not add a project reference between services.
2. **Events, not calls.** Cross-service effects go through Kafka. Stage the event as an `OutboxMessage` on the same `DbContext` and save once (ADR-028). Consumers dedupe with an inbox row.
3. **Read models.** Orders prices from `ordering.menu_replica` through `IRestaurantPricingSource` (ADR-037). Do not call the Restaurant service on the order path.
4. **Trace context.** Anything that publishes to Kafka carries `traceparent` (ADR-041).
5. **Keep the teaching comments.** Never delete `// (ADR-NNN)` comments. Add one when a change implements a decision.
6. **A new decision needs an ADR first** (`docs/adrs/`, next number 068). Ask before writing code.
