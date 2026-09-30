# ADR-053: Payment Tokenization (No PAN, Ever)

**Date:** 2026-07-13
**Status:** Accepted
**Deciders:** Tadka architecture team

## Context

A genuine checkout flow has a raw Primary Account Number (PAN) arrive at the Payment service boundary from the client. What happens to it in that first moment determines our entire PCI-DSS scope. ADR-024/026 isolated Payment into its own service and its own database specifically to shrink PCI scope — this ADR completes that story by ensuring the service never persists it, never logs it, and never lets it leave the tokenization step in raw form.

## Decision

`ChargeRequest.CardNumber` carries the raw PAN as a client would send it at checkout. The **very first thing** `PaymentService.ChargeAsync` does with
it is call `CardTokenizer.Tokenize`, which returns an opaque token (a **keyed HMAC-SHA-256** digest, prefixed
`TOK-`) and the last 4 digits — already public on the physical card and every receipt. Only the
token and last 4 are ever assigned to the `Payment` entity; the raw `cardNumber` parameter is
never referenced again after that line, is never logged (except via a restricted debug flag that must strictly never be enabled in production), and there is no PAN column anywhere in the `payment` schema.

Tokenization here is **one-way** (a keyed hash, not encryption) — there is no legitimate reason for
Tadka's Payment service to ever recover a raw card number once it has been charged.

**Why keyed.** A card number has far less entropy than its 16 digits suggest: the issuer's BIN (the first 6 to 8
digits) is public, the last 4 digits are stored right next to the token, and the final digit is a Luhn check digit.
That leaves roughly a million candidates per BIN, and the Luhn check discards 90% of them, so an *unkeyed* SHA-256 of a
card number can be brute-forced from a leaked table in well under a second. An HMAC keyed with a secret that is never
stored beside the tokens (`Demo:CardTokenizationKey`, configured once at startup by `CardTokenizer.Configure`, a dev-only
default in this repo and a KMS / secrets-manager value in production) removes that attack: without the key an attacker
cannot compute a single candidate digest to compare against a stolen token.

## Consequences

### Positive
- No PAN column exists in `payment.payments` at all.
- The token is deterministic per card (same digits -> same token), so a returning customer's
  saved card produces a stable identifier without ever re-touching the raw number.

### Negative
- A one-way token cannot be un-tokenized to retry a charge with the "same card" through a
  *different* payment gateway.
- The token is deterministic per card under ONE key, so two customers with the same physical card number get the same token (by design: it is how a saved card keeps a stable identifier). It also means **rotating the key changes every token**: a tokenization key needs key versioning (keep retired keys for lookup) or a re-tokenization migration, unlike a signing key whose old tokens simply expire. Tokens minted before the key existed would not match either.

### Risks
- If raw card data is accidentally logged during debugging, it instantly breaches PCI compliance. The raw PAN must strictly be scrubbed from all diagnostic output.

## Alternatives Considered

### Option A: Encrypt the card number (reversible) instead of tokenizing (one-way)
- Pros: simpler mental model (one pattern for all PII).
- Cons: encryption implies a legitimate reason to decrypt later — for a card number, Tadka never needs to show a user their full card number again; the last 4
  digits are sufficient. Keeping a decryptable PAN around is unnecessary risk surface.
- Why rejected: one-way tokenization is the correct, narrower tool for data you never need back.

### Option B: Don't accept `CardNumber` at all — assume the client already tokenized it
- Pros: removes the raw-PAN handling question from Tadka's Payment service entirely (push it to a
  client-side SDK, as real payment providers like Stripe/Razorpay actually recommend).
- Cons: legacy integrations and server-side checkouts might still require handling raw PANs temporarily.
- Why rejected: While moving to a client-side tokenization SDK is the ultimate goal, our server-side API must still securely handle raw PANs in the interim without persisting them.

### Option C: A separate `card_tokens` table / vault service
- Pros: closer to how a real tokenization vault is architected (separate trust boundary).
- Cons: added infrastructure for a project already isolating Payment into its own database.
- Why rejected: proportionate to current scale; the token lives on the `Payment` row itself, which
  is sufficient to prove no PAN exists anywhere in this schema.

## References
- ADR-024/026 (Payment extraction, database-per-service)
- ADR-052 (field-level PII encryption)
- `src/Tadka.Payment.Api/Infrastructure/CardTokenizer.cs`, `PaymentService.cs`.
