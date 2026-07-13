# ADR-045: Field-Level PII Encryption at Rest

**Date:** 2026-07-13
**Status:** Accepted
**Deciders:** Tadka architecture team

## Context

ADR-032 named column-level/at-rest encryption as policy ("adopted as policy + ADR; the
column-level wiring is noted, not fully built today"). This ADR closes that gap for one
representative field — `User.Phone` — and demonstrates the actual mechanism, rather than leaving
it as a documented intention. A `psql` query against `identity.users` today shows every phone
number in plaintext; anyone with read access to the database (a DBA, a backup file, a leaked
credential) sees raw PII with no additional barrier beyond the row existing in the table.

## Decision

`User.Phone` is encrypted at rest using AES-GCM (authenticated encryption — tamper-evident, not
just confidential) via an EF Core value converter. The ciphertext format is
`nonce (12 bytes) + tag (16 bytes) + ciphertext`, base64-encoded, with a fresh random nonce on
every write — encrypting the same phone number twice produces different ciphertext, so rows
can't be correlated by matching encrypted values.

The encryption key and an on/off switch (`Demo:EncryptPiiAtRest`, default `true`) are read once
at startup into a static `FieldCipher` class, before any `DbContext` model is built (the
migration-on-startup call happens later in `Program.cs`). This is a static configuration point
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
- Toggling the flag off reproduces the *previous* (plaintext) state exactly for the break demo,
  without maintaining two code paths — the converter is simply not applied.
- The column stays a fixed, generous width (250 chars) regardless of the flag, so flipping the
  toggle never requires a fresh migration.

### Negative
- **You cannot query on `Phone` at the database level anymore** — `WHERE "Phone" = '...'` in raw
  SQL, or any EF LINQ query filtering by phone, can no longer be pushed down to Postgres, because
  the ciphertext is non-deterministic (a different value every time, even for the same plaintext).
  Tadka doesn't query by phone today, so this is a real but currently-unrealized cost.
- The key lives in application configuration (a dev-only default in this codebase), not a real
  KMS. A real deployment must externalize it — named explicitly below, not hidden.
- `HasData` seed values are baked into the migration as ciphertext generated with whatever key was
  active at migration-generation time — if the key ever rotates, the seed data becomes unreadable
  unless it's re-seeded (a real operational concern for key rotation, named below).

### Risks
- If the key is lost, every encrypted phone number becomes permanently unreadable — this is
  crypto-shredding's OTHER edge: it's also a durability risk for legitimate data, not just a
  deliberate erasure tool (ADR-032 uses crypto-shredding intentionally for RTBF; here it's an
  accidental version of the same mechanism if key management is sloppy).

## Alternatives Considered

### Option A: Volume/disk-level encryption only (no column-level encryption)
- Pros: zero application code; protects against physical disk theft.
- Cons: does nothing against a logical access path — anyone with DB credentials, a `pg_dump`
  backup, or read access to a replica sees plaintext. This is the gap ADR-032 named and this ADR closes.
- Why rejected as the *only* layer: the threat model this ADR addresses (credentialed but
  unauthorized read access) is exactly what disk encryption doesn't cover.

### Option B: Database-native encryption (pgcrypto `pgp_sym_encrypt`)
- Pros: encryption happens in Postgres itself; no application-side key management inside the app process.
- Cons: the key still has to live somewhere the database can reach it (often a GUC or an
  extension parameter, which has its own exposure surface); queries and index behavior on
  encrypted columns need pgcrypto-specific SQL, not portable EF LINQ.
- Why rejected here, named for later: a legitimate production alternative, but the EF value
  converter keeps the encryption boundary in application code, consistent with how the rest of
  this codebase reasons about its data (portable across databases, testable without a live DB
  connection for the crypto logic itself).

### Option C: Encrypt every PII field (Name, Email, Address, geo) immediately
- Pros: maximal protection, matches the letter of "PII should be encrypted."
- Cons: `Email` has a unique index (`HasIndex(u => u.Email).IsUnique()`) that a non-deterministic
  ciphertext would break entirely — encrypting it requires either deterministic encryption
  (weaker, enables correlation) or restructuring the uniqueness constraint. Disproportionate
  scope for what this ADR needs to demonstrate.
- Why rejected: `Phone` is not indexed or queried today, making it the safe, representative field
  to encrypt first without hitting the Email-uniqueness complication. `Address`/`Email` are named
  as the next candidates, not built here.

## Teaching fields

- **Topic:** field-level (column) encryption at rest for PII, via an EF Core value converter.
- **Options:** volume encryption only | pgcrypto (DB-native) | EF value converter (chosen) | encrypt every PII field immediately.
- **Choice:** AES-GCM via an EF value converter, one representative field (`Phone`) first.
- **Why:** closes the exact gap ADR-032 named, demonstrates the real mechanism live (not just
  policy), and avoids the Email-uniqueness complication by choosing an unindexed field first.
- **Trade-off:** the field becomes unqueryable at the database level; the key must be managed
  outside this codebase in any real deployment.
- **Failure mode** (2 AM during dinner rush): a support engineer runs `WHERE "Phone" = '+91...'`
  directly against the database to find a customer's account and gets zero rows — not because the
  customer doesn't exist, but because the column is now ciphertext. The fix is a lookup by `Id` or
  `Email` (still queryable), not `Phone`; this needs to be documented for whoever's on call.
- **Revisit when:** a real deployment needs the key in a KMS (AWS KMS / Azure Key Vault / GCP
  KMS) with envelope encryption instead of a config value; or when `Address`/`Email` also need
  encrypting, at which point the Email-uniqueness trade-off (deterministic encryption, or move
  uniqueness to a separate hashed-lookup column) needs its own decision.
- **Cross-stack equivalents:** Java/Spring — a JPA `AttributeConverter` (the direct analogue of an
  EF value converter) or Hibernate's `@ColumnTransformer`; Node/Prisma — middleware or a custom
  field resolver wrapping `crypto.createCipheriv`/`createDecipheriv` (Node has no built-in ORM
  converter hook as clean as EF's, so this is typically hand-rolled at the repository layer); Go —
  a `sql.Scanner`/`driver.Valuer` pair on a custom type, the same shape as this ADR's converter.

## References
- ADR-032 (PII & data protection) — the policy this ADR implements the missing piece of.
- ADR-046 (payment tokenization) — a related but distinct control: encryption is reversible (with
  the key); tokenization here is one-way and never reversible, because there is no legitimate
  reason to ever recover a raw card number from Tadka's Payment service.
- `src/Tadka.Api/Infrastructure/Security/FieldCipher.cs`, `Data/Configurations/UserConfiguration.cs`.
- `cohort-prep/day-10/break-kit-day-10.md` Beat 5 — the captured before/after evidence.
