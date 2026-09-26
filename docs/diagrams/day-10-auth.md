# Day 10 — Authentication & Authorization Diagrams

Complete visual flow of JWT authentication, asymmetric RS256/JWKS per-service validation, RBAC + resource ownership, and atomic refresh-token rotation with reuse detection.

> **Deep Dive Reading:** [`docs/learn/token-and-refresh-flow.md`](../learn/token-and-refresh-flow.md)
>
> **Related Architecture Decision Records:**
> - [ADR-030: Stateless JWT Authentication](../adrs/030-stateless-jwt-auth.md)
> - [ADR-031: RBAC and Resource Ownership](../adrs/031-rbac-and-resource-ownership.md)
> - [ADR-032: PII Encryption and Right-to-be-Forgotten](../adrs/032-pii-masking-and-encryption.md)
> - [ADR-048: Refresh-Token Rotation & Reuse Detection](../adrs/048-refresh-token-rotation-reuse-detection.md)
> - [ADR-049: RS256 Asymmetric Signing & JWKS Key Rotation](../adrs/049-rs256-jwks-key-rotation.md)

---

## 1. Login → RS256 JWT & Refresh Token → Per-Service JWKS Validation

```mermaid
sequenceDiagram
    autonumber
    participant C as Client
    participant M as Monolith (/auth + Orders)
    participant DB as Postgres (identity)
    participant P as Payment service

    C->>M: POST /api/v1/auth/login (email + password)
    M->>DB: Verify credentials + check lockout
    M->>M: Sign RS256 JWT (sub, role, email, jti, restaurantId)
    M->>DB: INSERT SHA256(rawRefreshToken) with new FamilyId
    M-->>C: { accessToken, expiresInSeconds: 900, role, refreshToken }

    Note over C,M: High-frequency API calls use Access Token (15-min TTL)
    C->>M: POST /api/v1/orders (Authorization: Bearer <jwt>)
    M->>M: Validate RS256 signature (in-memory key) → RBAC + Ownership → 201 Created

    Note over C,P: Payment service validates independently without DB or shared secret
    C->>P: GET /payments/{id} (Authorization: Bearer <jwt>)
    alt Public key cached (< 5 min)
        P->>P: Resolve public key from memory cache
    else Cold cache or rotated kid
        P->>M: GET /.well-known/jwks.json
        M-->>P: { keys: [ { kid, n, e } ] }
    end
    P->>P: Verify RS256 signature → 200 OK / 401 Unauthorized
    Note over M,P: Zero shared secrets: verifiers hold only public keys (ADR-049)
```

---

## 2. Authorization Decision Tree (401 vs 403)

```mermaid
flowchart TD
    A[Incoming HTTP Request] --> B{Valid & Unexpired JWT?}
    B -- No / Missing --> R401[401 Unauthorized<br/><i>'Who are you?'</i>]
    B -- Yes --> C{Role Allowed?<br/>RBAC check}
    C -- No --> R403a[403 Forbidden<br/><i>'Known role, but not permitted'</i>]
    C -- Yes --> D{Owns the Resource?<br/>sub / restaurantId or Admin}
    D -- No --> R403b[403 Forbidden<br/><i>'Not your resource'</i>]
    D -- Yes --> OK[200 / 201 OK<br/><i>Request Allowed</i>]

    classDef blue fill:#3B82F6,stroke:#60A5FA,color:#F8FAFC
    classDef red fill:#EF4444,stroke:#F87171,color:#FFFFFF
    classDef green fill:#22C55E,stroke:#4ADE80,color:#0F172A
    classDef slate fill:#1E293B,stroke:#94A3B8,color:#E2E8F0

    class A slate
    class B,C,D blue
    class R401,R403a,R403b red
    class OK green
```

> **HTTP Semantics:**
> - **401 Unauthorized:** Authentication failure (*Who are you?* — missing, invalid, or expired token).
> - **403 Forbidden:** Authorization failure (*You are known, but not allowed* — insufficient role or accessing another tenant's data).

---

## 3. Silent Refresh & Single-Use CAS Rotation

```mermaid
sequenceDiagram
    autonumber
    participant C as Client
    participant M as Monolith (/auth/refresh)
    participant DB as Postgres (identity.refresh_tokens)

    C->>M: POST /api/v1/auth/refresh { "refreshToken": "raw_v1" }
    Note over M: hash = SHA256("raw_v1")
    
    M->>DB: UPDATE identity.refresh_tokens<br/>SET RevokedAt = NOW()<br/>WHERE TokenHash = @hash AND RevokedAt IS NULL AND ExpiresAt > NOW()
    
    alt 1 row claimed (Success)
        DB-->>M: 1 row claimed (Atomic CAS winner)
        M->>DB: INSERT new token (raw_v2) with SAME FamilyId
        M->>M: Mint fresh RS256 Access Token (15 min)
        M-->>C: 200 OK { accessToken: new_jwt, refreshToken: "raw_v2" }
    else 0 rows claimed
        DB-->>M: 0 rows claimed
        M->>M: Check if revoked token was replayed (See Diagram 4)
    end
```

---

## 4. Replay Attack Detection & Family Revocation

```mermaid
sequenceDiagram
    autonumber
    actor Attacker
    participant M as Monolith (/auth/refresh)
    participant DB as Postgres (identity.refresh_tokens)
    actor LegitimateUser

    Note over LegitimateUser: Rotated raw_v1 -> holds raw_v2
    Note over Attacker: Replays stolen raw_v1

    Attacker->>M: POST /api/v1/auth/refresh { "refreshToken": "raw_v1" }
    M->>DB: Atomic CAS claim (RevokedAt IS NULL)
    DB-->>M: 0 rows claimed (raw_v1 already revoked!)

    M->>DB: SELECT * FROM identity.refresh_tokens WHERE TokenHash = SHA256(raw_v1)
    DB-->>M: Row exists AND RevokedAt != null AND ExpiresAt > now
    
    Note over M,DB: 🚨 THEFT SIGNAL: Revoked token was replayed! 🚨
    M->>DB: UPDATE identity.refresh_tokens SET RevokedAt = NOW() WHERE FamilyId = @familyId
    DB-->>M: Entire session family revoked 💥

    M-->>Attacker: 401 Unauthorized {"error": "Invalid refresh token."}

    Note over LegitimateUser: Next background refresh with raw_v2:
    LegitimateUser->>M: POST /api/v1/auth/refresh { "refreshToken": "raw_v2" }
    M->>DB: Atomic CAS claim
    DB-->>M: 0 rows claimed (killed by family revocation)
    M-->>LegitimateUser: 401 Unauthorized (Forces full re-login)
```
