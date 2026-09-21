# ADR-049: RS256 Signing + JWKS Key Rotation (replaces the shared HS256 secret)

**Date:** 2026-09-21
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

ADR-030 chose HS256 "now, for simplicity" and explicitly named the migration path: "the auth
service holds the private key, every other service verifies with the public key, so a compromised
service can verify but not mint tokens." A prior audit flagged that Day-10 never took that step —
`Tadka.Api` and `Tadka.Payment.Api` both hold the literal same `Jwt:SigningKey` string in their
`appsettings.json`, in plaintext, in source control. This is worse than it looks on paper: **any
service (or config leak) that can verify a token can also forge one**, and rotating the secret
means coordinating a simultaneous config change across every service that trusts it — with zero
downtime tolerance, since a stale copy on either side breaks auth outright. This ADR closes both
gaps: asymmetric signing (only the monolith can mint), and a real key-rotation mechanism (JWKS)
instead of "change a shared string everywhere at once."

## Decision

**RS256, with the monolith publishing its publish keys at `/.well-known/jwks.json`; every verifier
(including the monolith itself, and `Tadka.Payment.Api`) resolves by `kid` against that published
set instead of holding a copy of a secret.**

- **`SigningKeyStore`** (`Tadka.Api`, in-memory): holds one or more RSA-2048 keypairs, each with a
  random `kid`. One is always "current" (used to sign new tokens); rotating generates a fresh
  keypair, makes it current, and retires the oldest once the store holds more than **current + 1
  previous** (a fixed, small retention cap — see Trade-off).
- **In-memory only, not file-backed.** Keys are generated fresh on process start and live only in
  that process's memory. **This is a real, named trade-off** (see Negative below), chosen for
  teaching-demo simplicity over the alternative (a gitignored `keys/` directory persisting keys
  across restarts) — a production deployment would use neither: an HSM or a managed KMS (see
  Revisit-when), not a local file either way.
- **`GET /.well-known/jwks.json`** (anonymous): publishes every currently-valid-for-verification
  key (current + any still-in-grace-window retired ones) in standard JWK Set format — the same
  shape `https://accounts.google.com/.well-known/openid-configuration`-style discovery uses.
- **`POST /api/v1/auth/rotate-signing-key`** (`[Authorize(Roles = "Admin")]`, reusing this
  codebase's existing RBAC pattern from ADR-031): generates a fresh keypair, makes it current,
  drops the oldest once over the cap.
- **`Tadka.Api`'s own `AddJwtBearer`** resolves signing keys via an `IssuerSigningKeyResolver`
  closure over the SAME `SigningKeyStore` instance the JWKS endpoint reads from — the monolith does
  NOT special-case "trust myself," it goes through the identical `kid`-keyed public-key lookup
  every other verifier uses. It just doesn't need an HTTP hop to reach its own in-memory store.
- **`Tadka.Payment.Api`'s `AddJwtBearer`** resolves via a NEW `JwksClient`: fetches
  `{Jwt:JwksBaseUrl}/.well-known/jwks.json` over HTTP, caches the parsed public keys in memory for
  **5 minutes** (`Jwt:JwksCacheMinutes`), and does one forced refetch on a cache-miss `kid` (it may
  have just been rotated in) before giving up. The resolver callback itself is synchronous (an
  ASP.NET Core JWT-bearer constraint) — it blocks on the cache lookup, which is normally instant;
  only a cold cache or a cache-miss triggers an actual (blocking) HTTP call.
- **Why JWKS-over-HTTP specifically, not a shared PEM file:** this is the literal pattern
  Auth0/Keycloak/Entra ID/Okta use for OIDC token verification at real scale — a verifier never
  holds a secret, never needs a deploy to pick up a rotated key, and the same discovery endpoint
  works for any number of verifying services without per-service config beyond "here's the URL."

## Consequences

### Positive
- **Blast-radius reduction is real, not cosmetic:** `Tadka.Payment.Api` (or any future service)
  can now be fully compromised — source, config, memory dump — without gaining the ability to MINT
  a token, only verify one. Only `Tadka.Api`'s process memory holds a private key.
- **Key rotation no longer requires touching every verifying service's config.** Rotate once on the
  monolith; every verifier picks up the new public key within its own cache TTL, no coordinated
  deploy.
- **A grace window (current + 1 previous) means rotation doesn't instantly 401 tokens issued
  moments earlier** — a real operational requirement any rotation scheme needs (see the integration
  tests: a token survives ONE rotation, then correctly fails after a second).

### Negative
- **In-memory-only keys mean a monolith restart invalidates every previously-issued access token's
  signing key.** With the (short, 15-minute) access-token TTL from ADR-030, this is bounded — the
  affected tokens simply expire soon regardless — but it IS a real availability wrinkle a rolling
  deploy needs to be aware of: a token signed by the pre-restart process, if the OLD key is gone
  from the NEW process's memory, fails to verify. A refresh (ADR-048) issues a fresh token signed
  by the new process's current key, so a client that refreshes on a 401 recovers transparently;
  one that doesn't (or is mid-flight on a long-lived access token) sees a spurious 401.
- **`Tadka.Payment.Api` now has a genuine network dependency to verify ANY token** — it did not
  before (a shared secret needs no network call). If `Tadka.Api`'s JWKS endpoint is unreachable
  exactly when `Tadka.Payment.Api`'s cache has just gone stale (past its 5-minute TTL) AND a
  never-before-seen `kid` shows up, verification fails closed (401) until the endpoint recovers —
  named honestly in Failure mode below, not hidden.
- Slightly more moving parts than a shared secret: a key store, a JWKS endpoint, an HTTP client +
  cache on the consuming side, versus one config string.

### Risks
- The `IssuerSigningKeyResolver`'s blocking `.GetAwaiter().GetResult()` call (required because the
  callback is synchronous) could, in the worst case (cold cache + a slow/hanging JWKS endpoint),
  tie up a thread-pool thread per concurrent request needing a fresh fetch. Mitigated in practice by
  the cache (almost every request is a cache hit) but named as a real risk under a JWKS-endpoint
  outage combined with a traffic spike, not dismissed.

## Alternatives Considered

### Option A: Stay HS256, rotate the shared secret manually when needed
- Pros: zero new code.
- Cons: doesn't fix the actual audit finding (any verifier can still forge); rotation still
  requires a coordinated, zero-downtime-sensitive config change across every service.
- Why rejected: doesn't close the gap the audit named.

### Option B: RS256 with a shared PEM file (the public key distributed as a file/config value,
copied to every verifying service)
- Pros: fixes the "verifier can forge" problem (verifiers only ever hold the PUBLIC key); no new
  HTTP endpoint or client needed.
- Cons: rotation is back to "coordinate a config change + redeploy across every verifying service
  simultaneously" — the exact operational pain HS256 already had, just with a public key instead of
  a secret. Doesn't scale past a handful of services either.
- Why rejected: solves the forging problem but not the rotation-coordination problem; JWKS-over-HTTP
  solves both.

### Option C: RS256 with JWKS-over-HTTP (chosen)
- Pros: no shared secret anywhere; rotation requires touching only the signing service; the
  discovery pattern scales to any number of verifiers with zero per-verifier config beyond a URL;
  it's the literal pattern real IdPs (Auth0, Keycloak, Entra ID, Cognito) use.
- Cons: a new network dependency for every verifier, a cache-TTL-shaped window of "briefly stale if
  the signer is unreachable," more moving parts than a static secret.
- Why chosen: it is the actual production pattern for exactly this problem, and Tadka is explicitly
  simulating a multi-service deployment (ADR-024/026) where "redeploy every service together"
  doesn't scale as a rotation story.

## Teaching fields

- **Topic:** asymmetric JWT signing (RS256) + public-key discovery/rotation (JWKS), replacing a
  shared HS256 secret across services.
- **Options:** stay HS256 | RS256 + a shared PEM file | RS256 + JWKS-over-HTTP (chosen).
- **Choice:** RS256; the monolith holds the private key; every verifier (including itself) resolves
  the public key by `kid` from `/.well-known/jwks.json`, cached with a 5-minute TTL on the
  consuming side.
- **Why:** this is what real IdPs do, it fixes the actual "any verifier can forge" audit finding,
  and it turns key rotation from a coordinated multi-service deploy into a one-service action.
- **Trade-off:** a new network dependency for every verifier; in-memory-only keys don't survive a
  monolith restart (bounded by the short access-token TTL + refresh, but real).
- **Failure mode** (2 AM during dinner rush): `Tadka.Api` (or just its JWKS endpoint) is briefly
  unreachable — a deploy, a network blip — at the exact moment `Tadka.Payment.Api`'s 5-minute cache
  has gone stale AND a request arrives with a `kid` not in that stale cache (e.g. right after a
  rotation). That ONE request's `IssuerSigningKeyResolver` gets no key back, and the token fails
  verification (401) — **not a crash, a fail-closed 401**, and it self-heals the instant the
  endpoint is reachable again (no manual intervention, no stuck bad state). Every request against a
  `kid` already in cache is completely unaffected the whole time. This is an honest, bounded
  failure mode, not glossed over as "won't happen."
- **Revisit when:** move signing keys into a real KMS/HSM (AWS KMS, Azure Key Vault, GCP KMS —
  "sign" becomes an API call the private key material never leaves) once this stops being a
  teaching demo; federate to a real external IdP (Auth0/Keycloak/Entra ID) once "sign in with an
  enterprise IdP" or multi-tenant federation is in scope — at that point Tadka stops minting its own
  tokens entirely and this whole ADR is superseded, the way ADR-030 anticipated for OAuth2/OIDC.
- **Cross-stack equivalents:** Java/Spring — Spring Security's `NimbusJwtDecoder.withJwkSetUri(...)`
  does exactly this resolver-by-kid-with-caching out of the box; Node — `jwks-rsa`'s
  `passport-jwt`/`express-jwt` integration (a `JwksClient` with the same TTL-cache shape); Go —
  `github.com/MicahParks/keyfunc` (a `jwt.Keyfunc` backed by a JWKS URL, same pattern). JWKS
  discovery-by-kid-with-cache is a standard, not a .NET-specific mechanism — every major JWT library
  ships a version of this resolver.

## References
- ADR-030 (JWT authentication — named this exact RS256/JWKS migration as the intended path; see the
  short superseding note added there), ADR-031 (per-service defense-in-depth validation — unchanged
  in spirit, the verification MECHANISM changes from a shared secret to a fetched public key, the
  decision to validate independently per-service does not).
- `src/Tadka.Api/Auth/SigningKeyStore.cs`, `Jwks.cs` (JWK Set (de)serialization), `TokenService.cs`,
  `AuthController.cs` (`rotate-signing-key`), `Program.cs` (JWKS endpoint + resolver wiring).
- `src/Tadka.Payment.Api/Auth/JwksClient.cs`, `JwksOptions.cs`, `Program.cs` (resolver wiring).
- `tests/Tadka.Api.Tests/Integration/JwksTests.cs` (real token ↔ own-JWKS round trip, rotation
  grace-window + cap), `tests/Tadka.Payment.Api.Tests/JwksValidationTests.cs` +
  `FakeJwksServer.cs`/`RealAuthPaymentApiFactory.cs` (Payment.Api's real `AddJwtBearer` +
  `JwksClient` pipeline against a rotating key set).
