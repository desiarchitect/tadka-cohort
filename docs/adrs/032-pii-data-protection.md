# ADR-032: PII Data Protection

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

The Tadka platform processes significant amounts of Personally Identifiable Information (PII), including names, email addresses, phone numbers, delivery locations, and financial data. Regulatory frameworks (such as GDPR, DPDP, and PCI-DSS) mandate strict handling, protection, and eventual deletion of this data upon user request (Right to Be Forgotten). In our event-driven architecture, data deletion is especially challenging because events containing PII can become immutable entries in distributed logs like Kafka.

## Decision

We will implement a defense-in-depth approach to PII, treating it as a first-class architectural constraint.

1. **Data Classification:** All schema fields will be explicitly classified. 
   - *Sensitive*: Phone, Email, Delivery Addresses, Geo-location.
   - *Restricted*: Payment card data (isolated entirely in the Payment service to minimize PCI scope).
   - *Public*: Restaurant names, menus.
2. **Event Minimization:** We will enforce a strict rule to minimize PII in event payloads. Events will carry aggregate identifiers (e.g., `UserId`, `OrderId`) rather than raw PII (emails, phone numbers). Services needing PII must fetch it via secure, authenticated APIs.
3. **Log Masking:** Application logs will utilize automated redactors to mask fields classified as Sensitive or Restricted before they are written to centralized logging systems, preventing accidental PII leaks.
4. **Right to Be Forgotten (RTBF):** We will implement data anonymization (tombstoning) rather than hard deletion. A user deletion request will overwrite name, email, and phone fields with generic tombstone values. This fulfills privacy requirements while preserving referential integrity for financial and order history records.

## Consequences

### Positive
- **Compliance:** Establishes a foundation for GDPR/DPDP compliance from the start.
- **Reduced Leakage:** Masking prevents the most common vector for data leaks (application logs).
- **System Integrity:** Anonymization avoids the cascading failures and reconciliation issues caused by hard deletes in relational databases.

### Negative / Risks
- **Operational Discipline:** Minimizing PII in events requires continuous engineering discipline and rigorous code reviews.
- **Complex Deletion Requirements:** If regulators reject tombstoning for specific datasets, we will be forced to implement complex crypto-shredding (encrypting specific records and throwing away the key).

## Alternatives Considered
- **Hard Deletion for RTBF:** Rejected. Hard deleting user records breaks foreign key constraints in order and payment histories, making financial reconciliation impossible.
- **Universal Column-Level Encryption:** Encrypting all PII at rest via application-level column encryption was rejected due to severe impacts on database indexing and query performance. We will rely on disk-level encryption (KMS) combined with selective application-level encryption only if strictly required.

## Revisit When
We will evaluate explicit crypto-shredding and advanced column-level encryption when standing up production cloud infrastructure. We will also revisit the anonymization strategy if regulatory audits mandate verifiable cryptographic erasure over tombstoning.
