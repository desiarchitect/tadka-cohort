# ADR-059: Backpressure (Admission Control)

**Date:** 2026-07-13  
**Status:** Accepted  
**Deciders:** Tadka Engineering Team  

## Context

During the recent dinner rush spikes, traffic exceeded our backend capacity. Because the monolith accepted every incoming TCP connection and HTTP request, the .NET thread pool starved, and the PostgreSQL connection pool became completely exhausted. This resulted in spiraling latency, memory bloat from queued requests, and ultimately a cascading failure where even lightweight health checks failed. The load balancer then marked the instances as dead, turning a spike into a total outage.

## Decision

We will implement strict admission control via a **Backpressure Middleware** to enforce a maximum concurrency limit.

- We wrap the incoming request pipeline in a Semaphore constrained by the `Backpressure:MaxConcurrent` configuration value (defaulting to 0 for off).
- If the number of in-flight requests hits this limit, the middleware immediately rejects incoming requests with an `HTTP 429 Too Many Requests` (or `503 Service Unavailable`) and a `Retry-After` header.
- This happens *before* the request reaches the routing layer, avoiding any DB connection or complex object allocation.

## Consequences

### Positive
- **Protects Infrastructure:** By shedding excess load at the front door, the server guarantees it has enough resources (threads, memory, DB connections) to successfully serve the requests it *does* admit. Latency for admitted requests remains flat.
- **Prevents Cascading Failures:** Instances stay alive and healthy instead of crashing under load, allowing the system to recover instantly when the traffic spike subsides.

### Negative / Risks
- **Poor Client UX if unhandled:** If client applications (mobile/web) do not respect the `Retry-After` header or lack exponential backoff, they will surface errors to the user or spam the server even harder.
- **Tuning Difficulty:** Finding the exact `MaxConcurrent` number is difficult. If set too low, we waste hardware capacity and reject revenue-generating traffic unnecessarily; if set too high, we still crash.

## Alternatives Considered

- **Unbounded Queuing (Default Behavior):** Let the OS/web server queue requests until resources free up. *Rejected:* Queued requests still consume memory, and by the time they are finally processed, the client has often already timed out, meaning we waste CPU doing work the user will never see.
- **Auto-scaling:** Relying strictly on adding more AWS ECS containers. *Rejected:* Auto-scaling takes minutes to spin up new instances and attach them to the load balancer. Traffic spikes (e.g., push notification campaigns) hit in seconds. Backpressure keeps the system alive *while* auto-scaling provisions more capacity.

## Revisit When
When we move to an advanced API Gateway (e.g., Kong, Envoy) or a Service Mesh, this concurrency limiting can be offloaded entirely to the proxy layer, removing the need for application-level semaphore middleware.
