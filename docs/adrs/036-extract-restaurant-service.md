# ADR-036: Extracting the Restaurant Service

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

The Restaurant domain (catalog, menus, availability) is the final major component residing in the monolithic application. Unlike the Payment extraction (driven by fault isolation) or the Delivery extraction (driven by a unique scaling profile), the Restaurant domain does not suffer from performance bottlenecks. It is read-heavy and heavily cached. 

The driver for this extraction is organizational and architectural alignment (Conway's Law). To allow independent deployment cadences and to align system boundaries with team ownership, the catalog and menu management lifecycle must be decoupled from core transactional ordering. 

However, extracting this service presents a critical architectural challenge: Order creation is the most important business flow, and it requires synchronous access to the Restaurant catalog to accurately price items and validate availability server-side.

## Decision

We will extract the Restaurant domain into its own independent service (`Tadka.Restaurant.Api`).

1. **Independent Infrastructure:** The service will have its own process, database, and deployment pipeline. The existing Redis cache used for menu data will move alongside this service, as the Restaurant domain owns that data.
2. **Move, Not Rewrite:** The migration will lift and shift existing logic, endpoints, and data structures to minimize risk.
3. **No Synchronous Coupling on the Hot Path:** We will absolutely **not** execute synchronous HTTP calls from the Ordering service to the Restaurant service during order creation. Doing so would tightly couple the availability of the critical order path to the catalog service, severely degrading system resilience. 
4. **Resolution:** The challenge of synchronous reads on the order path is addressed via Event-Carried State Transfer and a Local Read Model, documented explicitly in **ADR-037**.

## Consequences

### Positive
- **Organizational Alignment:** The team owning the catalog can deploy updates, change schemas, and scale independently of the core ordering team.
- **Architectural Clarity:** Completes the transition to a canonical distributed architecture where every distinct domain owns its data and lifecycle.

### Negative / Risks
- **Operational Overhead:** Introduces another database and service to monitor and maintain.
- **Complex Data Synchronization:** The monolithic ordering path is now fundamentally dependent on data it does not natively own, requiring robust asynchronous data replication mechanisms (ADR-037).
- **Migration Complexity:** Moving the catalog out of the monolith requires a complex, zero-downtime data migration strategy (ADR-038).

## Alternatives Considered
- **Keep Restaurant in the Monolith:** Rejected. While it is technically simpler and avoids the data synchronization problem, it prevents the organization from scaling its engineering teams independently and fails to achieve the target architectural state.
- **Extract with Synchronous HTTP Reads:** Rejected. If the Restaurant service experiences latency or downtime, the Ordering service would fail to create orders. We cannot compromise the availability of revenue-generating flows for organizational decoupling.

## Revisit When
If organizational dynamics change such that a single team manages both Ordering and Catalog operations long-term, the overhead of maintaining this boundary may outweigh the benefits, and reintegration could be considered.
