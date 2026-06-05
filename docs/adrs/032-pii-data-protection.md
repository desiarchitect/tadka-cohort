# ADR-032: PII & Data Protection (classification, masking, encryption, right-to-be-forgotten)

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

A food-delivery app holds real **PII**: name, **email**, **phone**, **delivery addresses + geo-location**, and (in the Payment service) card-adjacent data. Auth (ADR-030/031) controls *who reads* it, but data protection is broader: how it's classified, encrypted, kept out of logs, and **deleted on request**. Indian DPDP / GDPR-style "right to be forgotten" and PCI-DSS make this a Day-1 architecture constraint, not a feature — and our event-driven design (Kafka, Outbox) makes deletion genuinely hard.

## Decision

**Treat PII as a classified, first-class concern across the system.**
1. **Data classification:** label fields — *Sensitive* (phone, email, address, geo), *Restricted* (card data — isolated in the Payment service since Day 8, which **shrinks PCI scope**), *Public* (restaurant name, menu). Classification drives every rule below.
2. **Encryption:** **in transit** = TLS everywhere; **at rest** = DB/volume encryption, with **column-level** encryption (pgcrypto / app-side) or **crypto-shredding** for the most sensitive fields — *adopted as policy + ADR; the column-level wiring is noted, not fully built today* (it's a DB/KMS concern, Week 6/7 infra).
3. **PII masking in logs:** a redactor masks PII before it's logged (`+91••••••1234`, `a***@x.com`) — logs and traces are a top leak vector. This is **built and demoed**.
4. **Right to be forgotten:** `POST /api/v1/users/{id}/forget` **anonymises** the user (overwrite name/email/phone/addresses with tombstones, keep the row for order-history integrity) rather than hard-deleting (orders must still reconcile). **Honest limit:** events already emitted to **Kafka / the Outbox** can't be retro-scrubbed — so we **minimise PII in events** (events carry IDs + amounts, not phone/address) and rely on **crypto-shredding** (delete the key) for anything unavoidable. Built + demoed (anonymise); the event-history limit is taught, not hand-waved.

## Consequences
**Positive:** a clear classification → concrete rules; masking stops the most common real-world leak (PII in logs); RTBF is honest about what's actually reversible; PCI scope already reduced by the Payment extraction. **Negative/Risks:** column encryption complicates queries/indexing (you can't index an encrypted column normally) — hence selective; anonymise-not-delete is a deliberate trade (history integrity over true erasure) that must be defensible to a regulator; minimising PII-in-events is a discipline the whole team must hold. **Cost:** masking + an anonymise endpoint = code; real at-rest encryption + KMS = infra (Week 6/7).

## Alternatives Considered
- **Ignore PII until "later":** the default failure — PII ends up in logs, in every event payload, and undeletable. Rejected (it's a Day-1 constraint).
- **Hard delete on RTBF:** breaks order-history/financial reconciliation and referential expectations; anonymise/tombstone is the standard. Rejected.
- **Encrypt everything at rest, column-level, now:** kills query/index performance and is mostly redundant with volume encryption; we encrypt *selectively* by classification.

## Cross-stack equivalents
PII masking ≈ Serilog/`Destructurama` (.NET) · **Logback/Logstash masking** (Java) · pino redaction (Node) · zap hooks (Go). At-rest/column encryption ≈ **pgcrypto** (any stack) · cloud **KMS** + envelope encryption · Hibernate `@ColumnTransformer` / EF value converters. RTBF/anonymise + crypto-shredding is a pattern, language-neutral. Policy: classification + minimise-PII-in-events applies to every stack and every broker.

## References
- ADR-024/026 (Payment isolation → PCI scope), ADR-027/028 (events/outbox — the deletion boundary), ADR-030/031 (who may read PII)
- `cohort-prep/day-10/option-space.md`, `break-kit-day-10.md` (masked logs; anonymise demo)
- Implementation: log-masking redactor; `users/{id}/forget` anonymise endpoint; PII-field annotations

## Revisit When
Wire **real at-rest/column encryption + KMS** at deployment (Week 6/7). Re-audit **PII-in-events** whenever a new event/topic is added. Formalise a **data-retention** policy + an inbox/outbox **pruning** job. Revisit anonymise-vs-delete if a regulator requires true erasure (→ crypto-shredding by design).
