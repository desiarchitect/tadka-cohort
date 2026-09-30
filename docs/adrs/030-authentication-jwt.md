# ADR-030: Authentication — JWT Bearer Tokens

**Date:** 2026-06-05
**Status:** Accepted — **partially superseded, see note below**
**Deciders:** Tadka Engineering Team

> **2026-09-21 update:** the two gaps this ADR explicitly named as future work are now closed.
> Refresh-token rotation ("documented, not wired") is wired in **ADR-066**. Signing switches from
> HS256 to **RS256 + JWKS discovery** in **ADR-067** (the "migration path to RS256" this ADR
> flagged). The mechanism CHOICE below (JWT over sessions/OAuth2/etc.) and the claims shape
> (`sub`/`role`/`restaurantId`) are unchanged and still authoritative — only the signing algorithm
> and the refresh story have moved; read ADR-066/062 for those. Login is also now rate-limited and
> account-lockout-protected (**ADR-065**).

## Context

The Tadka platform previously lacked a formal authentication mechanism; internal endpoints were callable without identity verification. As the system scales into a distributed architecture with multiple independent services (Ordering, Payment, etc.), we need a robust, scalable way to securely verify the identity of the caller ("who is calling?") across process boundaries without introducing a centralized bottleneck.

## Decision

We will implement stateless authentication using **JSON Web Tokens (JWT)**. 

- **Token Issuance:** An authentication service/module will expose endpoints (`POST /api/v1/auth/register` and `/login`) to validate credentials against hashed passwords and issue signed JWTs.
- **Payload:** The JWT will contain essential claims: `sub` (User ID), `role` (Customer, Owner, Admin), and `restaurantId` (where applicable).
- **Verification:** Services will verify tokens statelessly using a shared signing key, meaning no service needs to call back to a central identity provider or database to validate a request.
- **Token Lifecycle:** We will use short-lived access tokens (e.g., 15 minutes) paired with longer-lived refresh tokens (e.g., 7 days). Refresh tokens will be stored server-side and rotated upon use to mitigate token theft.
- **Cryptography:** We will initially use HS256 (symmetric) for simplicity, with a planned migration to RS256 (asymmetric) to allow services to verify tokens without possessing the signing key.

## Consequences

### Positive
- **Stateless Verification:** Services can validate requests locally, preventing the identity system from becoming a single point of failure or performance bottleneck.
- **Horizontal Scalability:** Perfectly suited for our expanding microservices landscape and external client applications (mobile/web).
- **Bounded Blast Radius:** Short expiration times limit the window of vulnerability if an access token is compromised.

### Negative / Risks
- **Revocation Complexity:** Stateless tokens cannot be easily revoked before expiration. We mitigate this via the short 15-minute TTL.
- **Secret Management:** The symmetric signing key (HS256) becomes a highly sensitive secret that must be securely distributed to all verifying services.
- **Refresh Token Overhead:** Managing the storage, validation, and rotation of refresh tokens introduces stateful complexity to the authentication service.

## Alternatives Considered
- **Stateful Sessions (Cookies):** Rejected because it requires a shared session store (like Redis) across all microservices, coupling them to a central infrastructure piece.
- **OAuth2/OIDC (Delegated Auth):** Considered overkill for the primary internal auth mechanism at this stage. It remains a valid addition later for "Sign in with Google/Apple" features.
- **API Keys / mTLS:** Best suited for machine-to-machine communication, not for passing end-user context across services.

## Revisit When
We will migrate to asymmetric signing (RS256) with a proper Secrets Manager before production release. If immediate token revocation becomes a strict compliance requirement, we will implement a centralized token denylist. We will evaluate passkeys/WebAuthn when prioritizing phishing-resistant authentication.
