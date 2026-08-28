# ADR-007: Two-Layer Validation

**Date:** 2026-05-30
**Status:** Accepted
**Deciders:** Tadka Engineering Team

**Topic:** Where do we reject a bad write — at the HTTP edge, in the domain, or both?

**Options:**
1. Data annotations on DTOs only.
2. Domain invariants only (constructors throw; API is a pass-through).
3. Manual `if` walls in every controller.
4. FluentValidation at the boundary **and** invariants on the aggregate (two layers, two status codes).

**Choice:** Option 4. Shape (required fields, formats, empty arrays) → FluentValidation → **400**. Meaning (illegal transition, unavailable item) → entity / factory → **422**. Same RFC 7807 envelope (ADR-006).

**Why:** Kafka consumers and jobs (later weeks) will not hit controllers. If the rule lives only in the API, the side door writes garbage. If the rule lives only in the domain, a missing field still pays a DB round trip. 400 vs 422 is the Segment 2 split: "do I understand this request?" vs "does the rule allow it?"

**Trade-off:** Two places to update when a field is renamed. "quantity > 0" can appear twice for two reasons — it looks like duplication; it is not.

**Failure mode:** Devs skip domain checks because the API already validated. Then a consumer inserts an order with zero lines. Or error text from deep in the domain cannot be mapped to a field.

**Revisit when:** A third entry point (consumer, admin CLI) appears — the domain layer must still refuse. Do not "simplify" by deleting layer two.
