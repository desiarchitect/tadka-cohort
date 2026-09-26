# Day 10 — Runbook: Authentication, Authorization & PII

**Branch:** `day-10`  ·  **What's new:** through Day 9 the system was wide open; now it is secured. **JWT login with RS256/JWKS** (ADR-030/049), **RBAC + resource ownership** validated **per-service** (ADR-031, defense in depth — Payment independently verifies tokens and ownership), and **PII protection** (ADR-032 — log/response masking + GDPR right-to-be-forgotten). Same infra as Day 9.

> New here? Read [`README.md`](README.md). Windows PowerShell → `curl.exe`. Demo password for every seeded account: **`Password123!`**.

---

## 1. Run it (infra + BOTH apps)

Start the shared infrastructure, then launch both services. The monolith seeds demo users into PostgreSQL on its first boot:

```bash
git checkout day-10
docker compose up -d                       # postgres 5432 + replica 5433 + redis 6379 + payment-db 5434 + kafka 9092 + kafka-ui 8090
docker compose ps                          # confirm all containers are healthy
```

In two separate terminals, run both applications:

```bash
# Terminal 1 — Payment service (validates JWT via JWKS from monolith)
dotnet run --project src/Tadka.Payment.Api    # :5240

# Terminal 2 — Core Monolith (Auth, Orders, Restaurants, Identity)
dotnet run --project src/Tadka.Api            # :5224  (seeds demo users on startup)
```

### Seeded Identities
Every demo account shares password **`Password123!`** so you can test authorization boundaries without credential friction:
- **`admin@tadka.test`** — System Admin (`Role: Admin`). Bypasses ownership checks.
- **`priya@tadka.test`** — Customer 1 (`CustomerId: c1b2c3d4-0001-4000-8000-000000000001`).
- **`rahul@tadka.test`** — Customer 2 (`CustomerId: c1b2c3d4-0002-4000-8000-000000000002`). Used to test cross-tenant boundaries.
- **`owner1@tadka.test`** — Restaurant Owner (`RestaurantId: a1b2c3d4-0001-4000-8000-000000000001` — Meghana Foods).
- **`owner2@tadka.test`** — Restaurant Owner (`RestaurantId: a1b2c3d4-0002-4000-8000-000000000002` — Truffles).

---

## 2. Demo 1 — Closing the Authentication Bypass (ADR-030)

Through Day 9, there was zero authentication in the system: anyone could call `POST /orders` and pass any arbitrary `customerId` in the JSON body. Today, all mutating endpoints require a signed JWT, while public read operations (browsing restaurants and menus) remain open.

### Step 1: Prove unauthenticated writes are rejected while public reads stay open
Execute `POST /orders` without an `Authorization` header, followed by `GET /restaurants`:

```bash
RID="a1b2c3d4-0001-4000-8000-000000000001"; ITEM="b1b2c3d4-0001-4000-8000-000000000001"
BODY="{\"customerId\":\"c1b2c3d4-0001-4000-8000-000000000001\",\"restaurantId\":\"$RID\",\"items\":[{\"menuItemId\":\"$ITEM\",\"quantity\":1}],\"deliveryAddress\":{\"line1\":\"x\",\"line2\":\"y\",\"city\":\"Bangalore\",\"pincode\":\"560066\",\"latitude\":12.9,\"longitude\":77.7}}"

# 1. Unauthenticated write → 401 Unauthorized:
curl -s -o /dev/null -w "POST /orders (no token): %{http_code}\n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY"

# 2. Public read → 200 OK (customers can browse menus without logging in):
curl -s -o /dev/null -w "GET /restaurants (public): %{http_code}\n" http://localhost:5224/api/v1/restaurants
```

**What this proves:** Authentication is applied intentionally per route. Financial and identity mutations require proof of identity (`401`), but reading restaurant listings does not require an account (`200`).

### Step 2: Log in and place an order with a valid JWT
Log in as Priya to receive a signed RS256 token, then submit the order with the bearer token:

```bash
TOKEN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" \
  -d '{"email":"priya@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')

curl -s -o /dev/null -w "POST /orders (with token): %{http_code}\n" -X POST http://localhost:5224/api/v1/orders \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY"   # 201
```

**Key Architectural Security Property:** Notice the payload contains `"customerId":"c1b2c3d4-0001..."`. Even if a malicious user alters that JSON body field to another customer's ID, `OrdersController` ignores body-supplied customer IDs for non-admins and extracts the authenticated caller's identity strictly from the verified token's `sub` claim (`User.UserId()`). Spoofing identity in the request body is impossible.

---

## 3. Demo 2 — RBAC vs. Resource Ownership (ADR-031)

A role check alone (`[Authorize(Roles = "Customer")]`) only answers: *"Is this caller a customer?"* It cannot answer: *"Does this customer own THIS specific order?"* Similarly, `[Authorize(Roles = "RestaurantOwner")]` lets an owner edit menus, but must never let Owner 1 edit Owner 2's restaurant.

### Step 1: Priya places an order and reads her own order
```bash
# Place an order and extract its generated ID:
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')

# Priya reads her own order → 200 OK:
curl -s -o /dev/null -w "Priya reads her order: %{http_code}\n" http://localhost:5224/api/v1/orders/$ORDER -H "Authorization: Bearer $TOKEN"
```

### Step 2: Rahul (a different customer) tries to read Priya's order
Log in as Rahul and attempt to read Priya's order ID:

```bash
RAHUL=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"rahul@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')

# Rahul tries to read Priya's order → 403 Forbidden:
curl -s -o /dev/null -w "Rahul reads Priya's order: %{http_code}\n" http://localhost:5224/api/v1/orders/$ORDER -H "Authorization: Bearer $RAHUL"
```

**Why this returns 403, not 401:** Rahul's token is valid and signed. His authentication succeeded, and he has the `Customer` role. But the resource ownership check (`order.CustomerId == User.UserId()`) fails. A role check alone would have leaked Priya's order to Rahul (BOLA / IDOR vulnerability); resource ownership prevents it.

### Step 3: Restaurant Owner 1 attempts to edit Restaurant Owner 2's menu
Owner 1 owns Meghana Foods (`...0001`). Attempt to patch a dish on Truffles' menu (`...0002`):

```bash
O1=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"owner1@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')

# Edit Truffles dish as Owner 1 → 403 Forbidden:
curl -s -o /dev/null -w "owner1 edits OTHER restaurant menu: %{http_code}\n" -X PATCH "http://localhost:5224/api/v1/restaurants/a1b2c3d4-0002-4000-8000-000000000002/menu/b1b2c3d4-0002-4000-8000-000000000002" \
  -H "Authorization: Bearer $O1" -H "Content-Type: application/json" -d '{"price":{"amount":1}}'
```

> **The 401 vs. 403 Rule:**
> - **401 Unauthorized:** Authentication failed. "Who are you?" (Missing, expired, or invalid token).
> - **403 Forbidden:** Authorization failed. "We know who you are, but you cannot touch this resource." (Wrong role or not the owner). `Admin` role bypasses ownership.

---

## 4. Demo 3 — Defense in Depth: Per-Service Validation (ADR-031)

A classic microservice anti-pattern is **Gateway-Only Authentication**: the gateway verifies the JWT, strips it, and forwards a plain header like `X-User-Id: 123` to internal services over the internal network. If an attacker breaches the network or calls the service port directly, they gain complete unauthorized access.

Tadka enforces **Per-Service Validation**: `Tadka.Payment.Api` on port `:5240` independently validates the JWT signature via JWKS (`/.well-known/jwks.json`) from `Tadka.Api`, without relying on a shared secret.

### Step 1: Hit Payment HTTP endpoint with no token
```bash
curl -s -o /dev/null -w "Payment GET (no token): %{http_code}\n" http://localhost:5240/payments/$ORDER   # 401
```

### Step 2: Hit Payment HTTP endpoint with Priya's valid token
```bash
curl -s -o /dev/null -w "Payment GET (with token): %{http_code}\n" http://localhost:5240/payments/$ORDER -H "Authorization: Bearer $TOKEN"   # 200 or 404
```

**What this proves:** Even if an internal service port is directly exposed, it cannot be called anonymously. The Payment service validates the cryptographic signature against the public JWKS endpoint independently. Furthermore, Payment checks that the caller either owns the order or has the `Admin` role (`403` if Rahul attempts to query Priya's payment record).

---

## 5. Demo 4 — PII: Response Masking & Right-To-Be-Forgotten (ADR-032)

Authentication controls access to APIs. **PII Protection** governs how sensitive data is exposed and retained. Even authorized users must not see raw personal data of other users, and customers have a legal right to erasure (GDPR / DPDP).

### Step 1: Inspect response masking for non-owners
When Rahul queries Priya's user profile, the API redacts email and phone numbers before returning the response:

```bash
curl -s http://localhost:5224/api/v1/users/c1b2c3d4-0001-4000-8000-000000000001 -H "Authorization: Bearer $RAHUL"
# Output: {"id":"c1b2c3d4-0001...","name":"Priya Sharma","email":"p***@tadka.test","phone":"+91••••••••01"}
```

Notice this is a `200 OK`, not a `403`. Rahul is allowed to query the delivery recipient, but sensitive contact information is masked in transit.

### Step 2: GDPR Right-to-be-Forgotten (Anonymization Tombstone)
Execute Priya's deletion request:

```bash
curl -s -o /dev/null -w "forget: %{http_code}\n" -X POST http://localhost:5224/api/v1/users/c1b2c3d4-0001-4000-8000-000000000001/forget -H "Authorization: Bearer $TOKEN"   # 204
```

Verify the database row in PostgreSQL:

```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \"Name\",\"Email\" FROM identity.users WHERE \"Id\"='c1b2c3d4-0001-4000-8000-000000000001';"
# Name: [deleted]
# Email: deleted+c1b2c3d4000140008000000000000001@tadka.invalid
```

**Why Anonymize instead of Hard Delete?** A hard `DELETE FROM users` would cascade-delete or orphan foreign keys in `orders`, destroying financial audit trails and historical reporting. Anonymization overwrites PII with placeholder tombstones while preserving referential integrity.

> **Production Reality (Event Sourcing / Kafka):** Past Kafka events already published to topics like `order-placed` cannot be retroactively edited. Architectural mitigations include:
> 1. **PII Minimization:** Do not include raw phone numbers or physical addresses in Kafka event schemas (publish IDs only).
> 2. **Crypto-Shredding:** Encrypt PII in event payloads with a per-user key; deleting the user's key renders historical event payloads unreadable.

---

## 6. Deep-Dive: Column-Level Encryption & Card Tokenization (ADR-052 / ADR-053)

Masking protects data in transit. Column-level encryption protects data **at rest** against compromised database backups, read-replica leaks, or rogue DBA access.

### Step 1: Direct database inspection shows ciphertext
Query PostgreSQL directly to observe how `Phone` is stored on disk:

```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \"Name\", \"Phone\" FROM identity.users;"
```

The database stores random AES-GCM ciphertext blobs (e.g. `4yIKUWKg4386lm9PVjfcsx...`).

Now read the profile via the API as the authenticated owner:

```bash
curl -s http://localhost:5224/api/v1/users/c1b2c3d4-0001-4000-8000-000000000001 -H "Authorization: Bearer $TOKEN"
# Decrypted transparently: "phone":"+919876500001"
```

**How it works:** An EF Core Value Converter (`FieldCipher`) encrypts on `SaveChanges()` and decrypts on materialization using an AES-256-GCM authenticated cipher with a unique initialization vector (nonce) per write.

**The Architectural Trade-Off:** Because every write uses a fresh random nonce, the same phone number produces different ciphertext every time. **Encrypted columns cannot be queried with `WHERE Phone = '...'` in SQL.** Database indexes on encrypted columns become useless for exact matching unless deterministic encryption or blind indexing is used.

### Step 2: One-Way Card Tokenization (PCI-DSS Compliance)
Phone numbers are reversible because customer support needs to contact the user. Credit card numbers (PANs) are strictly **one-way tokenized**:

```bash
ADMIN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"admin@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')

curl -s -X POST http://localhost:5240/payments/charge -H "Content-Type: application/json" -H "Authorization: Bearer $ADMIN" \
  -d '{"orderId":"11111111-1111-4111-8111-111111111111","amount":299.00,"currency":"INR","cardNumber":"4111 1111 1111 1111"}'
```

Inspect the `payment.payments` table in PostgreSQL:

```bash
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \"OrderId\",\"CardToken\",\"CardLast4\" FROM payment.payments WHERE \"OrderId\"='11111111-1111-4111-8111-111111111111';"
```

Output:
```
               OrderId                |      CardToken       | CardLast4 
--------------------------------------+----------------------+-----------
 11111111-1111-4111-8111-111111111111 | TOK-B98C07776E30E28A | 1111
```

**Zero PAN Retention:** Notice there is no card number column in `payment.payments`. The raw PAN is processed in memory, hashed to a keyed HMAC-SHA-256 token (`CardToken`), stores the non-sensitive `CardLast4`, and the raw number is discarded immediately.

### Step 3: Demonstrating the Logging Anti-Pattern
Enable the diagnostic demonstration lever:

```bash
Payment__LogRawCardNumber=true dotnet run --project src/Tadka.Payment.Api
```

Triggering a charge now prints the raw card number to standard out. This illustrates the catastrophic defect of accidental debug logging in production (which violates PCI-DSS Requirement 3). The default `false` lever prevents card numbers from ever entering log aggregation pipelines.

---

## 7. Run the Tests

Execute the automated test suite across both services:

```bash
dotnet test
```

### Expected Output: **70/70 passed, 0 failed**
- **`Tadka.Payment.Api.Tests` (18 passed):** Tests per-service 401 validation, JWKS public key resolution, card tokenization digests (`CardTokenizerTests`), and idempotency gates.
- **`Tadka.Api.Tests` (52 passed):** Tests JWT issuance, password hashing, RBAC + resource ownership enforcement (`OrderTrackingAuthorizationTests`), AES-GCM encryption round-trips (`FieldCipherTests`), login rate-limiting, and atomic refresh-token CAS rotation (`RefreshTokenServiceConcurrencyTests`).

---

## 8. Wiring Reference & Cross-Stack Architecture

### Where the code lives in Tadka:
- **JWT Issuance & Verification:** [`src/Tadka.Api/Auth/TokenService.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Auth/TokenService.cs), [`Jwks.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Auth/Jwks.cs), [`SigningKeyStore.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Auth/SigningKeyStore.cs).
- **Per-Service Validation:** [`src/Tadka.Payment.Api/Auth/JwksClient.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Payment.Api/Auth/JwksClient.cs) and `Program.cs` (`AddJwtBearer` with dynamic key resolver).
- **Ownership Gates:** Inline checks in [`OrdersController.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Controllers/OrdersController.cs) and [`RestaurantsController.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Controllers/RestaurantsController.cs) (`OwnsOrAdmin`).
- **PII Masking & RTBF:** [`UsersController.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Controllers/UsersController.cs), [`FieldCipher.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Infrastructure/Security/FieldCipher.cs).
- **Payment Tokenization:** [`CardTokenizer.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Payment.Api/Infrastructure/CardTokenizer.cs).

### Cross-Stack Implementation Matrix:

| Concern | .NET Core (This Repo) | Java (Spring Boot) | Node.js (TypeScript) |
|---|---|---|---|
| **Stateless AuthN** | `Microsoft.AspNetCore.Authentication.JwtBearer` + `TokenService` | `Spring Security` + `oauth2ResourceServer().jwt()` (`NimbusJwtDecoder`) | `jsonwebtoken` / `passport-jwt` |
| **RBAC** | `[Authorize(Roles = "...")]` | `@PreAuthorize("hasRole('...')")` | Express middleware / NestJS `@Roles()` |
| **Resource Ownership** | Inline comparison (`CustomerId == User.UserId()`) | `PermissionEvaluator` bean / ACL | **CASL** ability (`can('read', 'Order', { customerId: user.id })`) |
| **PII Response Masking** | Custom DTO mapping / Redactor | Jackson custom serializer / `@JsonSerialize` | `class-transformer` / Custom interceptor |
| **Column Encryption** | EF Core Value Converter (`FieldCipher`) | Hibernate `@ColumnTransformer` / JPA `AttributeConverter` | Prisma Client Extension / TypeORM subscriber |
| **PAN Tokenization** | Keyed HMAC-SHA256 (`CardTokenizer`) | Spring service with `Mac.getInstance("HmacSHA256")` | Node `crypto.createHmac('sha256', key)` |

---

## ✅ Done When

- [ ] `POST /orders` without token returns `401 Unauthorized`.
- [ ] `GET /restaurants` without token returns `200 OK` (public catalog browse).
- [ ] Login returns a signed RS256 JWT; placing an order with the token returns `201 Created`.
- [ ] Reading another customer's order returns `403 Forbidden` (RBAC role passes, resource ownership fails).
- [ ] Patching another restaurant's menu returns `403 Forbidden`.
- [ ] Calling `GET http://localhost:5240/payments/{id}` directly without token returns `401 Unauthorized` (defense in depth).
- [ ] Querying another user's profile returns `200 OK` with masked phone and email.
- [ ] Calling `/forget` anonymizes the PostgreSQL user row into `[deleted]` and `deleted+...@tadka.invalid`.
- [ ] Direct database query on `identity.users` shows encrypted ciphertext for `Phone`.
- [ ] Direct database query on `payment.payments` shows `CardToken` and `CardLast4`; no raw card column exists.
- [ ] `dotnet test` returns **70/70 passed**.

---

## Troubleshooting

- **Login returns 401 for a seeded account:** `AuthSeeder` runs only on startup. If database was created before Day 10, wipe volumes to trigger re-seeding: `docker compose down -v && docker compose up -d`, then run the monolith.
- **Payment service returns 401 for all valid tokens:** Payment fetches the public key from the monolith via JWKS. Confirm `Tadka.Api` is running on port `:5224` and verify `curl http://localhost:5224/.well-known/jwks.json` returns a valid keys array.
- **`FormatException` on startup (`not a valid Base-64 string`):** You toggled `Demo:EncryptPiiAtRest` without resetting the database volume. The database contains data encoded under the previous state. Reset volumes with `docker compose down -v && docker compose up -d`.
- **Unexpected 403 on order creation:** Non-admin users cannot place orders for other customer IDs. Ensure the token belongs to the user placing the order or use Priya's credentials.

➡️ **Next (Day 11):** Extract the **Delivery** service (with real-time location tracking via Redis-geo) and introduce the **API Gateway** (YARP) for edge rate-limiting and reverse proxying.
