# ADR-037: Local Read Model via Event-Carried State Transfer

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Following the extraction of the Restaurant service (ADR-036), the Ordering domain lost direct database access to menu and pricing data. Order creation requires this data to calculate prices server-side, enforcing a critical security invariant (clients cannot dictate prices). 

Querying the Restaurant service synchronously via HTTP during order creation introduces temporal coupling. If the Restaurant service goes down or experiences latency, the Ordering service cannot process transactions. We must maintain high availability for order intake while accessing data owned by another service.

## Decision

The Ordering service will maintain a **Local Read Model** (a read-only replica) of the restaurant catalog, synchronized via **Event-Carried State Transfer**.

1. **Local Replica:** The Ordering service will maintain its own optimized tables (e.g., `ordering.menu_replica`) storing just the data necessary for order validation (item IDs, prices, availability, restaurant status).
2. **Event Consumption:** The Restaurant service will publish domain events (`menu-updated`, `restaurant-updated`) whenever catalog data changes, utilizing the Outbox pattern to guarantee delivery.
3. **State Transfer:** These events will carry the full updated state of the entity (not just a notification). The Ordering service will consume these events and upsert the data into its local replica.
4. **Order Processing:** The order creation flow will read prices and availability exclusively from its local replica database, completely decoupling it from the Restaurant service's runtime availability.

## Consequences

### Positive
- **High Availability:** Order intake is entirely isolated from Restaurant service outages or latency spikes. The critical path depends only on local resources.
- **No Synchronous Callbacks:** Because events carry the required state, the Ordering service never needs to make an HTTP callback to the Restaurant service to fetch missing data.
- **Performance:** Local database reads are significantly faster than cross-network HTTP calls.

### Negative / Risks
- **Eventual Consistency:** There is a brief window (typically milliseconds to seconds) between a menu price change and the replica updating. Orders placed during this window may be processed with stale prices. 
- **Data Duplication:** Catalog data is duplicated across two databases, requiring storage overhead and introducing the risk of data drift if events are lost or mishandled.
- **Infrastructure Reliance:** This pattern relies heavily on the reliability of the Kafka message broker and the Inbox/Outbox implementation to guarantee exactly-once processing semantics.

## Alternatives Considered
- **Synchronous HTTP Call with Circuit Breaker:** Rejected. Even with fallbacks, a degraded Restaurant service directly impacts order processing rates. 
- **Shared Distributed Cache (Redis):** Rejected. Having two independent services read and write to the same cache violates bounded contexts and blurs data ownership responsibilities.
- **API Composition at the Gateway:** Rejected. Pushing domain logic (pricing and validation) into a thin API gateway is an architectural anti-pattern.

## Revisit When
If the business determines that eventual consistency in pricing is unacceptable (e.g., for dynamic surge pricing where prices must be exactly accurate at the millisecond of purchase), we will need to reconsider the architecture. In that scenario, we may accept synchronous coupling behind a strict circuit breaker, or shift the pricing logic entirely into the catalog domain.
