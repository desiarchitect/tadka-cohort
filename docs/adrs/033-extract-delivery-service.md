# ADR-033: Extracting the Delivery Service

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

The Delivery domain manages delivery agents, assignments, and real-time live location tracking. As the platform prepares to enable real-time tracking, the workload profile for this domain fundamentally changes. Rider location updates arrive at a high frequency (every few seconds per active agent). These updates require extremely fast, low-durability writes (only the latest location matters) and sub-millisecond read latency for customer tracking.

Mixing this high-frequency ephemeral data stream into the core Ordering relational database (Postgres) would cause severe resource contention, exhausting connection pools and degrading the performance of critical business flows like order creation. The Delivery domain exhibits a completely different scaling and durability profile compared to transactional ordering.

## Decision

We will extract the Delivery domain into its own independent service (`Tadka.Delivery.Api`). 

1. **Independent Infrastructure:** The service will have its own process, deployment lifecycle, and dedicated databases suitable for its workload.
2. **Event-Driven Integration:** The monolithic Ordering service will no longer directly manage delivery state. Instead, when an order is confirmed, Ordering publishes an `order-confirmed` event via the Outbox pattern. Delivery consumes this event, assigns an agent, and publishes a `delivery-assigned` event.
3. **Polyglot Persistence:** Delivery will utilize multiple datastores optimized for specific access patterns (detailed in ADR-034): an in-memory store for high-frequency location pings, and a relational DB for durable assignment history.

## Consequences

### Positive
- **Fault Isolation:** The high-volume telemetry ingestion for live locations will never impact or throttle order intake or payment processing.
- **Independent Scaling:** The Delivery service can scale horizontally to handle thousands of concurrent rider telemetry streams without requiring the monolithic database to scale.
- **Resilience:** If the Delivery service fails, core ordering and payment functionality remains available (customers can place orders, though live tracking degrades).

### Negative / Risks
- **Operational Complexity:** Introduces a new service and additional datastores to provision, monitor, and maintain.
- **Expanded Distributed Transactions:** The order fulfillment flow now spans Ordering, Payment, and Delivery, stretching the Saga choreography further and increasing the time for eventual consistency to resolve.

## Alternatives Considered
- **Keep Delivery in the Monolith:** Rejected. High-frequency location updates would overwhelm the primary database, putting revenue-generating order traffic at risk.
- **Extract but Use Only Postgres:** Storing ephemeral location pings in a relational database leads to massive table bloat and vacuuming overhead. Rejected.

## Revisit When
We will re-evaluate the overall system architecture once the final core domain (Restaurant/Catalog) is extracted. If the distributed workflow (Ordering -> Payment -> Delivery) becomes too complex to trace or manage via choreography, we will evaluate introducing Saga Orchestration.
