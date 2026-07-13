# ADR-052: Field-Level PII Encryption at Rest

**Date:** 2026-07-13
**Status:** Accepted
**Deciders:** Tadka architecture team

## Context

ADR-032 named column-level/at-rest encryption as policy. This ADR closes that gap for one
representative field — `User.Phone` — and implements the actual mechanism. A database query against `identity.users` today shows every phone
number in plaintext; anyone with read access to the database (a DBA, a backup file, a leaked
credential) sees raw PII with no additional barrier beyond the row existing in the table.

## Decision

`User.Phone` is encrypted at rest using AES-GCM (authenticated encryption — tamper-evident, not
just confidential) via an EF Core value converter. The ciphertext format is
`nonce (12 bytes) + tag (16 bytes) + ciphertext`, base64-encoded, with a fresh random nonce on
every write — encrypting the same phone number twice produces different ciphertext, so rows
can't be correlated by matching encrypted values.

The encryption key and an operational switch (`Security:EncryptPiiAtRest`, default `true`) are read once
at startup into a static `FieldCipher` class, before any `DbContext` model is built. This is a static configuration point
rather than constructor-injected configuration because `UserConfiguration` is discovered and
instantiated with a parameterless constructor by `ApplyConfigurationsFromAssembly` — there is no
DI container involved at that point in the model-building pipeline.

The application layer (`UsersController`) is unaffected: EF Core transparently encrypts on write
and decrypts on read via the converter, so the existing masking logic (`PiiMasker`, ADR-032)
still operates on the plaintext value the application sees — encryption-at-rest and
masking-in-transit are two independent, composable layers.

## Consequences

### Positive
- A database-level read (backup, replica, leaked credential, a curious DBA) no longer exposes the
  raw phone number — it sees an opaque base64 blob.
- Toggling the flag off reproduces the plaintext state exactly (for debugging or phased rollouts), without maintaining two code paths.
- The column stays a fixed, generous width (250 chars) regardless of the flag, so flipping the
  toggle never requires a fresh migration.

### Negative
- **You cannot query on `Phone` at the database level anymore** — `WHERE "Phone" = '...'` in raw
  SQL, or any EF LINQ query filtering by phone, can no longer be pushed down to Postgres, because
  the ciphertext is non-deterministic (a different value every time, even for the same plaintext).
  Tadka doesn't query by phone today, so this is a real but currently-unrealized cost.
- The key currently lives in application configuration, not a KMS. A production deployment must externalize it to a secure vault.
- `HasData` seed values are baked into the migration as ciphertext generated with whatever key was
  active at migration-generation time — if the key ever rotates, the seed data becomes unreadable
  unless it's re-seeded.

### Risks
- If the key is lost, every encrypted phone number becomes permanently unreadable. This is a durability risk for legitimate data requiring strict key management and backup procedures.

## Alternatives Considered

### Option A: Volume/disk-level encryption only (no column-level encryption)
- Pros: zero application code; protects against physical disk theft.
- Cons: does nothing against a logical access path — anyone with DB credentials, a `pg_dump`
  backup, or read access to a replica sees plaintext.
- Why rejected: the threat model this ADR addresses (credentialed but unauthorized read access) is exactly what disk encryption doesn't cover.

### Option B: Database-native encryption (pgcrypto `pgp_sym_encrypt`)
- Pros: encryption happens in Postgres itself; no application-side key management inside the app process.
- Cons: the key still has to live somewhere the database can reach it; queries and index behavior on
  encrypted columns need pgcrypto-specific SQL, not portable EF LINQ.
- Why rejected: the EF value
  converter keeps the encryption boundary in application code, consistent with how the rest of
  this codebase reasons about its data (portable across databases, testable without a live DB
  connection).

### Option C: Encrypt every PII field (Name, Email, Address, geo) immediately
- Pros: maximal protection.
- Cons: `Email` has a unique index (`HasIndex(u => u.Email).IsUnique()`) that a non-deterministic
  ciphertext would break entirely — encrypting it requires either deterministic encryption
  (weaker, enables correlation) or restructuring the uniqueness constraint.
- Why rejected: `Phone` is not indexed or queried today, making it the safe, representative field
  to encrypt first without hitting the Email-uniqueness complication.

## References
- ADR-032 (PII & data protection)
- ADR-046 (payment tokenization)
- `src/Tadka.Api/Infrastructure/Security/FieldCipher.cs`, `Data/Configurations/UserConfiguration.cs`.
