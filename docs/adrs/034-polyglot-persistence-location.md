# ADR-034: Polyglot Persistence for Live Location Data

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

The newly extracted Delivery service (ADR-033) handles two distinct types of data with conflicting requirements:
1. **Live Rider Location:** Highly ephemeral data. Agents ping their coordinates every few seconds. Read latency must be sub-millisecond for customer tracking interfaces. Only the absolute latest coordinate matters; historical pings are irrelevant for the live tracking use case.
2. **Delivery Assignments:** Highly durable transactional data. Records of which agent was assigned to which order, timestamps, and status changes. This data must survive restarts and support relational queries.

Storing the high-frequency location pings in a relational database like Postgres would result in constant row updates, massive Write-Ahead Log (WAL) generation, index bloat, and aggressive vacuuming pressure for data that is immediately obsolete upon the next ping.

## Decision

We will adopt a polyglot persistence strategy for the Delivery service, utilizing specialized datastores for specific workloads:

- **Redis (Geospatial) for Live Location:** We use Redis Geo commands `GEOADD` / `GEOPOS` for the live ping (O(log N), overwrite-latest, sub-ms). **Assignment itself is first-available** (`FirstOrDefault` on `Available`) — we do **not** call `GEOSEARCH` today. `GEOSEARCH` (nearest-N) is the named next move when proximity matching is earned; do not teach the shipped code as nearest-rider. Redis is ephemeral: a restart is repopulated by the next incoming pings within seconds.
- **Postgres for Assignment History:** We will use a standard relational database for durable, transactional records such as order assignments, agent status changes, and auditing data.

## Consequences

### Positive
- **Optimized Performance:** Location ingestion operates at memory speed without degrading relational database performance.
- **Resource Efficiency:** We avoid storing millions of useless historical ping records in an unbounded relational table.
- **Appropriate Durability:** Transactional assignment data maintains strict ACID guarantees in Postgres.

### Negative / Risks
- **Operational Overhead:** The team must operate and monitor two separate datastore technologies for a single service.
- **No Native Analytics:** Because live location data is continuously overwritten in Redis, historical analysis (e.g., average route times, heatmaps) is impossible by default.
- **Cognitive Load:** Developers must consciously decide which store is appropriate for new features in the Delivery domain.

## Alternatives Considered
- **Postgres / PostGIS Only:** Rejected due to write bloat and database connection exhaustion under high-frequency telemetry load. 
- **Redis Only:** Rejected because it lacks the relational integrity and durability required for financial and operational reconciliation of delivery assignments.
- **Time-Series Database:** Considered overkill for our current requirements, as we do not yet have a business mandate to analyze historical route telemetry.

## Revisit When
If the business requires historical analytics on rider routes, we will implement a background process to stream location data from Redis (or the ingestion pipeline) into a dedicated Data Warehouse or Time-Series Database.
