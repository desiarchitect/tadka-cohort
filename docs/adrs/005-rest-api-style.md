# ADR-005: REST for the Client-Facing API (and server-side prices)

**Date:** 2026-05-30
**Status:** Accepted
**Deciders:** Tadka Engineering Team

**Topic:** What is the public HTTP contract for web/mobile, and who is allowed to name the money on `POST /orders`?

**Options:**
1. REST/JSON over HTTP verbs. Client sends item ids + quantities; **server** reads the menu and computes the total. No price in the request. No PUT. No DELETE (deactivate / cancel).
2. REST, but trust or "verify" a client-sent price.
3. GraphQL (one endpoint, client picks fields).
4. gRPC to the browser (or gRPC-Web).
5. JSON-RPC (one URL, method names).

**Choice:** Option 1. Controllers, `/api/v1` resources. Domain actions that are not CRUD use explicit POST (`POST /orders/{id}/cancel`). Money is never a client field.

**Why:** CRUD is most of the surface; the team already speaks HTTP; curl and Scalar work. A client price is either tampering or a stale screen — verifying it still punishes the honest user whose menu was 25 minutes old. Only the server knows the price at order time.

**Trade-off:** REST can be chatty and over-fetch; cancel is RPC-shaped. Server-side pricing adds a menu read on the order path (~2 ms today, in-process). In Week 6 that read is a **network hop**: Restaurant owns our P99, and if it is down **no order can be placed**.

**Failure mode:** View-specific endpoints proliferate. Or we "just add price to the body" under time pressure and silent undercharge returns. Or we extract Restaurant without a local read model and take the whole order path down with it.

**Revisit when:** Restaurant is extracted — then a local read model (Week 6), not "put the price back on the client." Internal service calls may use gRPC later; the public app stays REST. GraphQL if multiple clients prove over-fetch is the bottleneck, not before.
