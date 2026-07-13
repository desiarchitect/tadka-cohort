# ADR-046: Payment Tokenization (No PAN, Ever)

**Date:** 2026-07-13
**Status:** Accepted
**Deciders:** Tadka architecture team

## Context

Before this ADR, `Payment` had no field for card data at all — the Payment service's Fake
Gateway doesn't need one, so the question of "where does the raw card number live" never came up
in the code. That's an accident of the Fake Gateway's simplicity, not a real answer: a genuine
checkout flow *does* have a raw PAN arrive at the Payment service boundary from the client at
some point, and what happens to it in that first moment is the entire PCI-DSS scope question.
ADR-024/026 already isolated Payment into its own service and its own database specifically to
shrink PCI scope — this ADR completes that story by making sure the one thing that actually
touches a card number never persists it, never logs it, and never lets it leave the tokenization
step in raw form.

## Decision

`ChargeRequest.CardNumber` carries the raw PAN as a client would send it at checkout — an
optional field (a charge can also arrive with an already-tokenized saved payment method,
carrying no raw number at all). The **very first thing** `PaymentService.ChargeAsync` does with
it is call `CardTokenizer.Tokenize`, which returns an opaque token (a SHA-256 digest, prefixed
`TOK-`) and the last 4 digits — already public on the physical card and every receipt. Only the
token and last 4 are ever assigned to the `Payment` entity; the raw `cardNumber` parameter is
never referenced again after that line, is never logged (except behind an explicit, default-off
demo lever proving what NOT to do), and there is no PAN column anywhere in the `payment` schema.

Tokenization here is **one-way** (a hash, not encryption) — there is no legitimate reason for
Tadka's Payment service to ever recover a raw card number once it has been charged. Contrast with
ADR-045's field encryption, which is reversible by design (the application needs the real phone
number back to call a customer).

## Consequences

### Positive
- No PAN column exists in `payment.payments` at all — verifiable directly with `\d payment.payments`.
- The token is deterministic per card (same digits -> same token), so a returning customer's
  saved card produces a stable identifier without ever re-touching the raw number.
- The demo lever (`Demo:LogRawCardNumber`) makes the anti-pattern this ADR prevents directly
  reproducible and visible, instead of asserting "we don't do that" without evidence.

### Negative
- A one-way token cannot be un-tokenized to retry a charge with the "same card" through a
  *different* gateway that doesn't recognize Tadka's token — a real payment processor's own
  vaulting/tokenization service is what a production system would integrate with instead of a
  hand-rolled SHA-256 token. This is a teaching-scale stand-in, named as such.
- `CardTokenizer` uses an unsalted hash of the digits — two different customers with the same
  physical card number (a genuinely rare but not impossible real-world case: shared corporate
  cards) would produce the same token. Acceptable for this codebase's scope; a production
  tokenization vault handles this correctly by design (it's not just a hash).

### Risks
- If `Demo:LogRawCardNumber` were ever accidentally left on in a real deployment, it would defeat
  the entire point of this ADR — it exists ONLY as a default-off teaching lever and must never
  ship enabled. (The same category of risk as any "debug logging" flag in any codebase.)

## Alternatives Considered

### Option A: Encrypt the card number (reversible) instead of tokenizing (one-way)
- Pros: symmetric with ADR-045's approach; simpler mental model (one pattern for all PII).
- Cons: encryption implies a legitimate reason to decrypt later — for a card number, there isn't
  one in this codebase (Tadka never needs to show a user their full card number again; the last 4
  digits are sufficient for any UI). Keeping a decryptable PAN around is unnecessary risk surface.
- Why rejected: one-way tokenization is the correct, narrower tool for data you never need back.

### Option B: Don't accept `CardNumber` at all — assume the client already tokenized it
- Pros: removes the raw-PAN handling question from Tadka's Payment service entirely (push it to a
  client-side SDK, as real payment providers like Stripe/Razorpay actually recommend).
- Cons: doesn't teach the actual mechanism a service must have when it DOES receive raw card data
  (which is still a real scenario — legacy integrations, server-side checkouts, etc.); this
  cohort's goal is to show the tokenization step itself, not just avoid the problem by assumption.
- Why rejected: the teaching value is in the boundary handling, not in defining it away. Named as
  the real production recommendation in the teaching fields below.

### Option C: A separate `card_tokens` table / vault service
- Pros: closer to how a real tokenization vault is architected (separate trust boundary, its own
  access controls).
- Cons: real added infrastructure for a project already isolating Payment into its own database;
  disproportionate for demonstrating the core lesson (raw PAN never persists).
- Why rejected: proportionate to project scale; the token lives on the `Payment` row itself, which
  is sufficient to prove no PAN exists anywhere in this schema.

## Teaching fields

- **Topic:** payment card tokenization and PCI-DSS scope reduction.
- **Options:** encrypt (reversible) | assume client tokenizes already | tokenize server-side (chosen) | separate vault service.
- **Choice:** one-way tokenization inside `PaymentService.ChargeAsync`, immediately on receipt.
- **Why:** demonstrates the actual boundary-handling mechanism; a one-way token is the correct,
  narrower tool since there's no legitimate reason to recover a raw PAN in this codebase.
- **Trade-off:** unsalted-hash tokenization is a teaching-scale stand-in, not a production vault;
  cannot un-tokenize for use with a different downstream gateway.
- **Failure mode** (2 AM during dinner rush): a well-meaning developer, debugging a failed charge,
  adds `logger.LogInformation("Charging card {Card}", cardNumber)` "just for this one investigation."
  That single line puts a raw PAN into every log aggregator, every log-shipping pipeline, and
  every engineer with log access — instantly out of PCI compliance. The demo lever
  (`Demo:LogRawCardNumber`) makes exactly this mistake reproducible, on purpose, so the room feels
  how easy it is to introduce and how invisible it looks in a code review that isn't specifically
  looking for it.
- **Revisit when:** integrating a real payment processor — replace `CardTokenizer` with the
  processor's own client-side tokenization SDK (Stripe Elements, Razorpay Checkout, etc.), so the
  raw PAN never reaches Tadka's servers AT ALL, which is the actual PCI-DSS SAQ-A-eligible
  best practice (Option B above, done properly with a real vendor).
- **Cross-stack equivalents:** the discipline (tokenize at the boundary, never log/persist raw
  card data) is entirely stack-agnostic — Java/Spring, Node, and Go all reach for the same shape
  (a boundary conversion function called before anything else touches the value), typically backed
  by the payment processor's own SDK rather than a hand-rolled hash in any real deployment.

## References
- ADR-024/026 (Payment extraction, database-per-service) — the isolation this ADR completes.
- ADR-045 (field-level PII encryption) — the reversible counterpart for data that IS legitimately
  needed back.
- `src/Tadka.Payment.Api/Infrastructure/CardTokenizer.cs`, `PaymentService.cs`.
- `cohort-prep/day-10/break-kit-day-10.md` Beat 5 — the captured before/after evidence.
