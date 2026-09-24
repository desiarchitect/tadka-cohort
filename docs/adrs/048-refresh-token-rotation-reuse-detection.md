# ADR-048: Refresh-Token Rotation + Reuse Detection

**Date:** 2026-09-21
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

ADR-030 named refresh-token rotation as the intended production strategy but explicitly shipped
Day-10 with it **"documented, not wired"** — `/login`/`/register` issued only a 15-minute access
token, with no way to get a new one short of logging in again. A prior audit flagged this as a real
gap: every client (mobile app, SPA) is forced to either re-prompt for a password every 15 minutes,
or — the actual failure mode teams reach for under that pressure — quietly lengthen the access
token's TTL, which widens the blast radius of a leaked token right back open (the exact thing
ADR-030 chose a short TTL to bound). This ADR wires the missing half.

## Decision

**Rotation with reuse detection** — a refresh token is single-use; presenting an already-used
(revoked) one is treated as a theft signal and kills the entire session, not just the replayed
token.

- **New `RefreshToken` entity** (`identity.refresh_tokens`): `Id`, `UserId`, `TokenHash`,
  `FamilyId`, `CreatedAt`, `ExpiresAt` (7 days), `RevokedAt` (nullable).
- **We hash the raw token, but NOT with `IPasswordHasher`.** A refresh token is a 256-bit
  `RandomNumberGenerator`-sourced secret — high-entropy by construction, unlike a human-chosen
  password — so it needs no per-value salt or deliberately-slow hash to resist guessing. What it
  DOES need is to be **looked up directly** when a client presents it (`WHERE TokenHash = @hash`,
  backed by a unique index) — `IPasswordHasher`'s hash is different every time by design (a fresh
  salt per call), which makes an indexed equality lookup impossible; you'd have to load every
  stored hash and re-verify each one. We use SHA-256 (`RefreshTokenService.Hash`) — fast,
  deterministic, and the raw token is never stored, only its digest, so a database read (backup,
  replica, leaked credential — the same threat ADR-045 names for PII) yields hashes, not tokens
  usable to steal a session.
- **`FamilyId` (a shared GUID), not a `ReplacedByTokenId` linked list.** Every token minted from one
  login (and every token it rotates into) carries the SAME `FamilyId`. Revoking a whole chain is
  one indexed bulk update — `UPDATE ... WHERE FamilyId = @id AND RevokedAt IS NULL` — instead of
  walking a chain of `ReplacedByTokenId` pointers token-by-token. Chosen for being simpler to get
  right under concurrent requests (no risk of a partially-walked chain) at the cost of losing the
  literal "who replaced whom" audit trail a linked list would give for free — we don't need that
  trail today; `CreatedAt`/`RevokedAt` timestamps are enough to reconstruct the sequence if needed.
- **`POST /api/v1/auth/refresh`**: claims the presented token with a single atomic
  `UPDATE ... SET RevokedAt = now() WHERE TokenHash = @hash AND RevokedAt IS NULL AND ExpiresAt > now()`
  (`ExecuteUpdateAsync`), not a read-then-write. **This matters under concurrency:** an earlier
  version of this code did `SELECT`, check `RevokedAt` in C#, then write it back much later via
  `SaveChangesAsync` — two simultaneous requests presenting the SAME token could both read
  `RevokedAt == null`, both pass the check, and both rotate, silently skipping reuse detection
  entirely (the exact "two tabs" scenario below, except undetected instead of a false positive).
  The atomic claim means only ONE concurrent caller can ever win the row.
  - Claim succeeds (1 row) → the presented token is revoked (single-use), a NEW token in the SAME
    family is issued, a new access token is minted, both returned. This is the rotation.
  - Claim affects 0 rows → **not automatically reuse.** Look the token up again (no filter this
    time): not found, or found-but-expired → generic `401`, family untouched — it's just an invalid
    token, not theft. Found AND not expired AND already revoked → **reuse detected.** The entire
    family is revoked immediately (`RevokeFamilyAsync`), and the response is still a generic `401`,
    identical to "not found," so a caller can never tell which case fired (ADR-047's same
    honesty-about-leakage stance).
- **`POST /api/v1/auth/logout`** (`[Authorize]`): revokes the caller's own refresh-token family
  (verified by matching `UserId` — one user cannot silently kill another's session by guessing/
  submitting their token). **Named limitation, not oversold:** the ACCESS token already handed out
  stays valid until its own short (15-minute) expiry — a stateless JWT can't be revoked mid-flight
  without extra infrastructure (a denylist, which reintroduces the statefulness ADR-030 chose JWT
  specifically to avoid). Logout here means "no more silent refreshes," not "instantly logged out
  everywhere."

## Consequences

### Positive
- Clients get a real session-continuation story: refresh silently in the background, no forced
  15-minute password re-prompt, without permanently widening the access token's TTL.
- Reuse detection turns a stolen-and-replayed refresh token from a silent, ongoing compromise into
  a **detected, chain-killing event** — both the thief's and the legitimate user's copies of the
  latest token stop working, forcing a fresh login (and, in a real deployment, a place to hang an
  alert: "refresh-token reuse detected for user X").
- The hash-not-hasher choice keeps `/refresh` a single indexed lookup — no perf cliff as the table grows.

### Negative
- One more table, one more migration, one more DB round-trip per refresh (a `SELECT` by hash + an
  `UPDATE` to revoke + an `INSERT` for the new token, all in the request path) — real but small
  cost for a low-frequency endpoint (called once per ~15 minutes per active session, not per request).
- A legitimate user's OWN retry logic (e.g. two tabs racing to refresh with the same stale token, or
  a client that retries a timed-out-but-actually-succeeded refresh call) can trigger a FALSE
  reuse-detection and force a re-login — named honestly: rotation-with-reuse-detection trades a
  small amount of client-side robustness for the security win. A production client needs to
  serialize its own refresh calls (a mutex/single-flight around "am I already refreshing?") to avoid
  self-inflicted false positives.

### Risks
- **A stolen-and-replayed refresh token, used FIRST by the thief:** the thief gets one valid
  rotation (a new access + refresh pair) before the legitimate user's own next refresh attempt hits
  an already-revoked token and triggers detection — at which point BOTH the thief's and the
  legitimate user's sessions die together. This is the honest limit of reuse detection: it catches
  theft on the SECOND use of a stolen token (whoever loses the race to use it first), not the first.
- **A database compromise exposing `TokenHash` values:** does NOT hand the attacker usable refresh
  tokens (SHA-256 isn't reversible, and the raw token was never stored) — but it DOES tell them
  which sessions exist and their expiry windows, which is metadata worth caring about even though
  it isn't a direct session-hijack.

## Alternatives Considered

### Option A: No refresh token (status quo — access token only)
- Pros: zero new code/table.
- Cons: exactly the gap this ADR closes — forced 15-minute re-login, or the temptation to widen the
  access token's TTL and undo ADR-030's blast-radius bound.
- Why rejected: the gap the audit named.

### Option B: Long-lived refresh token, no rotation (issue once, reuse until expiry)
- Pros: simplest possible implementation — one token, one DB row, checked on every refresh.
- Cons: a stolen refresh token is valid for its FULL lifetime (7 days here) with zero detection —
  the thief and the legitimate user both refresh happily forever, indistinguishable.
- Why rejected: no theft signal at all: this is the exact anti-pattern ADR-030 flagged rotation as
  the fix for.

### Option C: Rotation without reuse detection
- Pros: simpler than the full design — still single-use, still limits a stolen token's live window
  to one rotation cycle instead of 7 days.
- Cons: presenting an old, already-rotated token just fails quietly (expired/invalid) — no signal
  that something is actually wrong, no automatic full-chain revocation, so if a thief's copy and the
  legitimate user's copy diverge, whichever one refreshes SECOND simply gets a confusing "invalid
  token" with no indication their session was compromised versus a normal expiry.
- Why rejected: reuse detection is the cheap addition (one more `RevokedAt IS NULL` check already
  being read) that turns a silent failure into an actionable signal.

## Teaching fields

- **Topic:** refresh-token issuance, rotation, and theft (reuse) detection.
- **Options:** no refresh token | long-lived, no rotation | rotation only | rotation + reuse
  detection (chosen).
- **Choice:** single-use rotation with `FamilyId`-based chain revocation on reuse.
- **Why:** turns a stolen-and-replayed token from an unbounded, silent compromise into a bounded,
  detected one — at the cost of one extra table and one extra DB round-trip per refresh.
- **Trade-off:** added table + per-refresh DB round-trip; naive concurrent-client refresh logic can
  self-trigger false reuse detection.
- **Failure mode** (2 AM during dinner rush): a support ticket says "I got logged out for no
  reason." The honest diagnosis path: check whether TWO requests hit `/refresh` with the SAME token
  close together (a client bug — a race between tabs/retries) versus a genuine stolen-token replay
  from a different IP/user-agent — the `RefreshToken` row's `CreatedAt`/`RevokedAt` timestamps are
  the forensic trail, but this repo does not (yet) log WHICH case fired, by design (ADR-047's
  don't-leak-which stance) — a real deployment needs a server-side audit log (not returned to the
  caller) to actually distinguish these at 2 AM.
- **Revisit when:** add device/session metadata (user-agent, approximate location) to
  `RefreshToken` once "log out this OTHER device" or "here's everywhere you're logged in" becomes a
  product requirement — today logout only knows about the ONE family the presented token belongs to.
- **Cross-stack equivalents:** Java/Spring — Spring Authorization Server's built-in refresh-token
  rotation (`reuseRefreshTokens(false)`), or a hand-rolled entity nearly identical to this one on
  plain Spring Security; Node — most `passport`/custom-JWT setups hand-roll this exact table +
  hash-lookup pattern (no framework does it for you); Go — same, typically a `refresh_tokens` table
  behind `golang-jwt` + a manual rotation handler. The pattern — hash for lookup, family for
  chain-revocation — is not library-specific.

## References
- ADR-030 (JWT authentication — named this as the intended-but-unwired production strategy),
  ADR-047 (the sibling rate-limiting/lockout control on the same auth surface), ADR-045 (field-level
  hashing/encryption — the same "never store the raw secret" discipline applied here to tokens
  instead of PII).
- `src/Tadka.Api/Auth/RefreshTokenService.cs`, `AuthController.cs` (`/refresh`, `/logout`),
  `Domain/Users/RefreshToken.cs`, `Data/Configurations/RefreshTokenConfiguration.cs`,
  `Migrations/20260921133153_AddLoginLockoutAndRefreshTokens.cs`.
- `tests/Tadka.Api.Tests/Integration/RefreshTokenTests.cs` — rotation, reuse-kills-the-whole-chain,
  unknown-token rejection, logout, and the ownership check on logout.
