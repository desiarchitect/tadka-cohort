# ADR-060: Load Shedding by Priority

**Date:** 2026-07-13  
**Status:** Accepted  
**Deciders:** Tadka Engineering Team  

## Context

Following the implementation of Backpressure (ADR-059) and distributed rate limiting, our system now safely rejects traffic during massive spikes, preventing crashes. However, a naive rejection strategy drops requests uniformly. During a peak dinner rush where capacity is maxed out, it is unacceptable to drop an `Order Placement` or a `Payment Webhook` at the same rate we drop a background `Menu Refresh` or a client analytics ping. We must protect our revenue-generating paths over secondary features.

## Decision

We will implement **Priority-based Load Shedding**.

- When the `LoadShed:Enabled` flag is true (toggled manually during an incident or automatically via health metrics), a priority middleware inspects the route.
- **Critical paths** (authentication, order placement, payment ingestion, delivery tracking, and health checks) are admitted normally.
- **Non-critical paths** (menu browsing for unauthenticated users, user profile updates, past order history) are immediately rejected with an `HTTP 503 Service Unavailable`.

## Consequences

### Positive
- **Revenue Protection:** Maximizes business value during partial outages. Even if we are operating at 50% capacity, we ensure the capacity is used to complete inflight orders and accept new ones.
- **Graceful Degradation:** The application degrades predictably. Users can still place orders, even if they cannot view their past order history.

### Negative / Risks
- **Maintenance Overhead:** Developers must remember to categorize and tag every new endpoint they create as critical or non-critical. A misclassified endpoint could accidentally drop payments during an incident.
- **False Positives:** If the load shed trigger is too sensitive, we might degrade the user experience (blocking menu reads) when the system actually had capacity to serve it.

## Alternatives Considered

- **VIP User Tiering:** Shedding load based on the user's tier (e.g., drop free users, allow premium subscribers). *Rejected:* While useful for SaaS platforms, food delivery relies on completing transactions regardless of the user's historical value. Route-based priority is much simpler and directly protects the transaction.
- **Dynamic Cost-based Shedding:** Assigning a "cost" to each endpoint and rejecting expensive queries first. *Rejected:* Too complex to model and maintain. A binary critical/non-critical split covers 95% of the benefit with 5% of the effort.

## Revisit When
We implement dynamic, self-tuning concurrency limits (like TCP Vegas) where the system automatically sheds non-critical traffic as latency rises, rather than relying on a static binary toggle.
