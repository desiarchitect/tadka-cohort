# ADR-025: Synchronous HTTP for the First Inter-Service Bridge (why HTTP before Kafka)

**Date:** 2026-06-04
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Extracting Payment (ADR-024) raises the first distributed-systems question: **how do the monolith and the Payment service talk?** The monolith's background payment processor (ADR-023) needs to tell Payment "charge this order" and learn the outcome. The options run from a plain HTTP call to a full message broker — and the *wrong* instinct is to reach for Kafka immediately because "microservices use Kafka."

We are deliberately taking the cheapest transport that works, so that the next failure earns the next tool — the cohort's whole method.

## Decision

**Use synchronous HTTP/REST for the first bridge.** The Payment service exposes `POST /payments/charge` (idempotent by `orderId`) and `GET /payments/{orderId}`; the monolith calls it through a **typed `IPaymentClient` (HttpClient)** from the background processor. **The Day-7 Polly pipeline (timeout + bulkhead) is reused verbatim around the HTTP call** — the resilience pattern transfers from an in-process gateway call to a network hop with no conceptual change. Intake stays asynchronous (the processor is still off the request path), so `POST /orders` still returns in milliseconds.

Outcome handling:
- HTTP success → publish `PaymentCompletedEvent` → order auto-confirms.
- Business decline (HTTP 4xx-class result) → `PaymentFailedEvent` → order cancels.
- **Service unreachable / timeout → the charge is dropped from the in-memory queue and the order stays pending.** We leave this gap *visible on purpose*: it is the temporal-coupling failure that earns Kafka + a durable queue on Day 9.

## Consequences

### Positive
- **Simplest possible step:** both services already have Kestrel + `HttpClient`; no new infrastructure, no new mental model. The team can read an HTTP call.
- **Resilience reuse:** the same timeout + bulkhead now protect the network hop — a slow Payment service fails fast (~2 s) instead of hanging the processor.
- **Request/reply fits** "charge this order, tell me the result" cleanly.

### Negative / Risks
- **Temporal coupling:** caller and callee must be up at the same moment. If Payment is down, the synchronous call fails — and because the queue item is already consumed, the charge is **lost** and the order is stuck pending. (This is the demo that motivates Day 9.)
- No durability, no replay, no fan-out — all of which a broker gives.

### Cost (₹ / effort)
Near-zero: a typed client + two endpoints. The cost is the *correctness gap* above, paid down in Week 5 with Kafka + Outbox.

## Alternatives Considered
- **gRPC:** faster (binary, multiplexed, typed contracts) but adds `.proto` files, codegen, and HTTP/2 concerns that distract from the extraction lesson. Worth it later for chatty internal hops; not now.
- **Kafka now:** the right long-term answer for durable, decoupled, replayable events — but it introduces topics/partitions/consumer-groups/offsets/serialization, a broker to operate, and at-least-once + idempotency reasoning. That deserves its own session (Day 9), *earned* by the temporal-coupling failure this ADR deliberately leaves exposed.
- **HTTP forever:** the trap — at scale every order waits on a synchronous chain and a downstream outage cascades. The Day-9 move replaces the transport, not the boundary.

## Cross-stack equivalents
Typed `HttpClient` ≈ **Spring** `RestClient`/`WebClient`/OpenFeign · **Node** `fetch`/axios (+ a client wrapper) · **Go** `net/http` client. Reusing a resilience policy around the call ≈ **Resilience4j** `@TimeLimiter`+`Bulkhead` (Java) · **opossum** + `AbortController` (Node) · `context.WithTimeout` + a semaphore (Go). gRPC is the same cross-language (protobuf) alternative everywhere.

## References
- ADR-021 (the Polly pipeline reused here), ADR-023 (async processor that makes the call), ADR-024 (the extraction), ADR-026 (Payment's own DB)
- `cohort-prep/day-08/break-kit-day-08.md` (the "Payment service down → order stuck pending" demo)
- Implementation: monolith `Modules/Payments/IPaymentClient.cs` + `HttpPaymentClient.cs`; service `src/Tadka.Payment.Api`

## Revisit When
**Day 9 (Week 5):** replace the synchronous HTTP call with **Kafka** + the **Outbox pattern** — the monolith publishes `OrderPlaced` durably (committed in the order's transaction), Payment consumes at-least-once with an idempotent handler, and a down Payment service simply means messages wait, not lost charges. HTTP stays for genuine request/reply queries; commands go async.
