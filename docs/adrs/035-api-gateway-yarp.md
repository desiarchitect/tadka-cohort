# ADR-035: API Gateway (YARP) — one entry point for the services

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

After extracting Payment (Day 8) and Delivery (ADR-033), a client faces **three hosts** — monolith `:5224`, Payment `:5240`, Delivery `:52xx`. That means three base URLs to configure, CORS/port sprawl, no single place for cross-cutting edge concerns (rate-limiting, a uniform auth challenge, request logging, TLS termination), and the internal topology leaks to clients (every extraction breaks them). A single front door is now earned.

## Decision

**Introduce an API gateway with YARP (`Tadka.Gateway`)** — a reverse proxy that is the **one public entry point** (e.g. `:8080`):
- **Routing:** `/api/v1/payments/**` → Payment, `/api/v1/deliveries/**` → Delivery, everything else (`/orders`, `/restaurants`, `/auth`, `/users`) → the monolith. Clients know **one** host; the topology is hidden behind the gateway.
- **Edge concerns:** **rate-limiting** (ASP.NET `RateLimiter`) and a uniform place for request logging / TLS; the `Authorization` header is forwarded.
- **Defense in depth preserved:** the gateway is **not** a trust boundary — **per-service JWT validation (ADR-031) remains the floor.** The gateway may *also* validate at the edge (fail fast), but a service never trusts "the gateway already checked it."

## Consequences

### Positive
- One client-facing URL; internal extractions/moves don't break clients (route changes are gateway-side).
- A single home for edge rate-limiting, logging, TLS, CORS, and (later) request aggregation.
- Smooth path to the cloud equivalent (ALB + API Gateway) — same role, managed.

### Negative / Risks
- **A new hop** (latency + one more thing to run and make HA — a gateway outage is a front-door outage).
- Tempting to make it a **trust boundary** (validate-only-at-edge) → the forge-the-header breach (ADR-031). We explicitly keep per-service validation.
- Tempting to stuff business logic into the gateway → it must stay a thin edge (routing + cross-cutting only).

### Cost (₹ / effort)
A small YARP project + a routes config; near-zero locally. In the cloud it's a managed **ALB / API Gateway / App Gateway + APIM** (real cost — justify; sized by traffic).

## Alternatives Considered
- **No gateway (clients hit each service):** simplest, but leaks topology + no single edge for cross-cutting concerns; rejected at 3 services.
- **nginx / Envoy / Kong / Traefik:** all valid reverse proxies/gateways; YARP chosen because it's .NET-native (one stack, code-reviewable config) for the cohort — the *pattern* is identical. Kong/Envoy add a plugin/policy ecosystem when you outgrow a thin proxy.
- **Cloud-managed (AWS ALB + API Gateway, Azure App Gateway + APIM):** the production target — see the option-space; the *deployable* black-box lands on the Day-12 deploy day (needs a cloud account).

## Cross-stack equivalents
YARP ≈ **Spring Cloud Gateway** (Java) · **Express Gateway** / a Node proxy · **Kong / Envoy / Traefik / nginx** (any stack) · cloud-managed **AWS ALB + API Gateway**, **Azure Application Gateway + APIM**, **GCP**. "One thin edge for routing + cross-cutting; never a trust boundary" is the language-neutral rule.

## References
- ADR-024/033 (the extractions that create multiple hosts), ADR-031 (per-service validation stays the floor)
- `cohort-prep/day-11/option-space.md` (gateway options + cloud map + Terraform sketch), `break-kit-day-11.md`
- Implementation: `src/Tadka.Gateway` (YARP routes + rate-limiting)

## Revisit When
Move auth/rate-limit/TLS to a **cloud-managed gateway** at deployment (Day 12 / production). Adopt **Kong/Envoy** if you need a rich plugin/policy ecosystem or a service mesh. Add request aggregation / BFF only when a client genuinely needs it (don't pre-build).
