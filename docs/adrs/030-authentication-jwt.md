# ADR-030: Authentication — JWT (chosen from the full mechanism menu)

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Through Day 9 Tadka has **no authentication**: every endpoint (place/cancel order, edit menu, the Payment service's HTTP endpoints) is callable by anyone. The `User` model exists (`Email`, `Phone`, `PasswordHash`, `Role`) but nothing issues or checks identity. We must answer "**who is calling?**" — and we choose deliberately from the whole menu, not by reflex.

## The mechanism menu (teach the map, then pick)

| Mechanism | How it works | Best for | Why-not here |
|---|---|---|---|
| **Session / cookie** | server stores session, cookie holds id | a single web server / monolith | stateful → needs a shared session store across our extracted services |
| **JWT (bearer)** ✅ | signed, self-contained token; stateless verify | APIs, mobile, **multi-service** | revocation is harder (mitigated by short TTL + refresh rotation) |
| **OAuth2 / OIDC** | delegate identity to Google/Apple/… | "Sign in with X", third-party | overkill as the *primary* store for Tadka's own users (we'd still issue our own token after) |
| **API keys** | a static secret per client | server-to-server, public APIs | no user identity; coarse |
| **mTLS** | client cert proves identity | service-to-service in a mesh | heavy PKI; we're not in a mesh yet |
| **Passwordless / magic-link** | email/SMS one-time link | low-friction consumer login | a UX choice layered *on top* of token issuance |
| **Passkeys / WebAuthn (FIDO2)** | device-bound public-key cred | phishing-resistant future default | client/device support + flows beyond today's scope |
| **SSO / SAML** | enterprise IdP federation | B2B/enterprise | not Tadka's consumer model |

## Decision

**Issue JWTs from an auth endpoint in the monolith.** `POST /api/v1/auth/register` + `/login` validate credentials (password hashed with `PasswordHasher<User>`) and return a signed JWT whose claims carry `sub` (userId), `role`, and `restaurantId` (for owners). Services verify the token statelessly with a shared signing key.

- **Why JWT:** stateless verification works across the monolith **and** the extracted Payment service with **no shared session store** — each service checks the signature itself (the multi-service property sessions lack). Mobile/SPA-friendly.
- **Token strategy:** short-lived **access token (15 min)** + a **refresh token (7 days, stored server-side, rotated on use)**. A stolen access token is dangerous for ~15 min, not a day; refresh rotation detects reuse and revokes the chain.
- **Signing:** **HS256 (symmetric) now** for simplicity; **migration path to RS256 (asymmetric)** noted — the auth service holds the private key, every other service verifies with the public key, so a compromised service can verify but not mint tokens.
- Never store plaintext passwords; never put secrets/PII in the JWT payload (it's signed, not encrypted — it's readable).

## Consequences
**Positive:** stateless, scales horizontally, one verification path reused by every service; short TTL bounds the blast radius of a leaked token. **Negative/Risks:** JWT revocation before expiry is awkward (you wait out the 15 min or maintain a denylist); the signing key is now a critical secret (→ RS256 + a secrets manager); refresh-token storage + rotation is extra code. **Cost:** library + a refresh-token table; near-zero infra.

## Alternatives Considered
Sessions (rejected: stateful across services); OAuth2/OIDC (great for delegated "Sign in with Google", but we still mint our own token — add later as an *identity source*, not the core); API keys / mTLS (service-to-service, not user auth — relevant for the gateway/mesh later); passkeys (the phishing-resistant future — revisit when client support and flows are in scope).

## Cross-stack equivalents
ASP.NET `AddAuthentication().AddJwtBearer()` ≈ **Spring Security** (`oauth2ResourceServer().jwt()`) · **Node** Passport-JWT / **NextAuth** / `jsonwebtoken` · **Go** `golang-jwt` + middleware. Managed alternatives everywhere: **Keycloak / Auth0 / Cognito / Entra ID** (issue + rotate tokens for you). The *concept* — a signed, stateless, short-lived bearer token verified at each service — is identical.

## References
- ADR-031 (authorization on top of these claims), ADR-032 (PII — don't leak it in tokens/logs), ADR-024/026 (the extracted Payment service that must also verify)
- `cohort-prep/day-10/option-space.md` (the full mechanism + model menus), `break-kit-day-10.md`
- Implementation: monolith `Auth/*` (endpoints, token service, `PasswordHasher`), JWT validation in both services

## Revisit When
Move to **RS256 + a secrets manager** before production / when a 2nd service mints-or-verifies independently. Add **OAuth2/OIDC** when "Sign in with Google" is needed, **passkeys** when phishing-resistance is prioritized. Add a token **denylist** if immediate revocation becomes a requirement.
