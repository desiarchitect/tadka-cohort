# Day 10 — Runbook: Authentication, Authorization & PII

**Branch:** `day-10`  ·  **What's new:** through Day 9 the system was wide open; now it is secured. **JWT login with RS256/JWKS** (ADR-030/049), **RBAC + resource ownership** validated **per-service** (ADR-031, defense in depth — Payment independently verifies tokens and ownership), and **PII protection** (ADR-032 — log/response masking + GDPR right-to-be-forgotten). Same infra as Day 9.

> New here? Read [`README.md`](README.md). Demo password for every seeded account: **`Password123!`**.

Every command below is given twice, bash first and PowerShell second. These are not the same commands with `curl` swapped for `curl.exe`: bash's `VAR=$(...)`, `sed -E`, and inline `VAR=value command` syntax do not run in plain PowerShell at all. Windows PowerShell also has no built-in way to read an HTTP status code off a 401/403 response without the call throwing, so a small helper function is defined once below and reused throughout. Every PowerShell block here was run live against this branch before being written down.

---

## 1. Run it (infra + BOTH apps)

### Demo Day Fresh Reset (Clean Slate)
If you want to remove all existing containers, network overlays, and persistent database volumes to ensure a completely clean environment:

```bash
docker compose --profile auth-prod down -v --remove-orphans && docker compose up -d
docker compose ps
```
```powershell
docker compose --profile auth-prod down -v --remove-orphans; docker compose up -d
docker compose ps
```

*(This stops any running services including optional profiles like Keycloak, wipes volume directories `pgdata`, `pgdata_replica`, `payment_pgdata`, `redisdata`, and starts postgres 5432, replica 5433, redis 6379, payment-db 5434, kafka 9092, and kafka-ui 8090 fresh from scratch).*

---

### Standard Launch
If starting existing containers without wiping data:

```bash
git checkout day-10
docker compose up -d                       # postgres 5432 + replica 5433 + redis 6379 + payment-db 5434 + kafka 9092 + kafka-ui 8090
```
```powershell
git checkout day-10
docker compose up -d
```

Wait for Kafka specifically before starting either app. Kafka takes longer to start than Postgres or Redis, and `docker compose ps` alone does not block on it:
```bash
until docker inspect tadka-kafka --format "{{.State.Health.Status}}" | grep -q healthy; do sleep 3; done
```
```powershell
do { Start-Sleep -Seconds 3 } until ((docker inspect tadka-kafka --format "{{.State.Health.Status}}") -eq "healthy")
```

Pre-create the two Kafka topics this branch uses. On a fresh broker with no topics yet, each app subscribes the moment it starts, and if one starts before the other has ever published anything, you will see `Confluent.Kafka.ConsumeException: Subscribed topic not available` logged once a second. It is harmless (the consumer keeps retrying and picks up the topic once it exists) but looks alarming on a first run:
```bash
for t in order-placed payment-results; do
  docker exec tadka-kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --create --if-not-exists --topic $t --partitions 1 --replication-factor 1
done
docker compose ps                          # confirm all containers are healthy
```
```powershell
foreach ($t in "order-placed","payment-results") {
  docker exec tadka-kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --create --if-not-exists --topic $t --partitions 1 --replication-factor 1
}
docker compose ps
```

In two separate terminals, run both applications (identical command, either shell):

```
# Terminal 1 — Payment service (validates JWT via JWKS from monolith)
dotnet run --project src/Tadka.Payment.Api    # :5240

# Terminal 2 — Core Monolith (Auth, Orders, Restaurants, Identity)
dotnet run --project src/Tadka.Api            # :5224  (seeds demo users on startup)
```

### PowerShell helper: reading a status code without the call throwing
Every demo below needs to check a status code like `401` or `403`. In bash, `curl -s -o /dev/null -w "%{http_code}"` does this without complaint. In PowerShell, `Invoke-WebRequest`/`Invoke-RestMethod` throw an exception on any non-2xx response, so getting the code back requires catching that exception and reading it off. Define this once, in the same terminal session you'll run every PowerShell block below in:
```powershell
function Get-StatusCode {
    param($Uri, $Method = "GET", $Headers = @{}, $Body = $null, $ContentType = "application/json")
    try {
        $params = @{ Uri = $Uri; Method = $Method; Headers = $Headers; UseBasicParsing = $true }
        if ($Body) { $params.Body = $Body; $params.ContentType = $ContentType }
        $r = Invoke-WebRequest @params
        return [int]$r.StatusCode
    } catch {
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
        else { throw }
    }
}
```
Verified live: returns `401`/`403`/`200` correctly for every case below, instead of the script stopping on the first rejected call.

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
```powershell
$RID = "a1b2c3d4-0001-4000-8000-000000000001"
$ITEM = "b1b2c3d4-0001-4000-8000-000000000001"
$BODY = @{
  customerId = "c1b2c3d4-0001-4000-8000-000000000001"
  restaurantId = $RID
  items = @(@{ menuItemId = $ITEM; quantity = 1 })
  deliveryAddress = @{ line1="x"; line2="y"; city="Bangalore"; pincode="560066"; latitude=12.9; longitude=77.7 }
} | ConvertTo-Json -Depth 5

# 1. Unauthenticated write → 401 Unauthorized:
Write-Host "POST /orders (no token):" (Get-StatusCode -Uri "http://localhost:5224/api/v1/orders" -Method Post -Body $BODY)

# 2. Public read → 200 OK (customers can browse menus without logging in):
Write-Host "GET /restaurants (public):" (Get-StatusCode -Uri "http://localhost:5224/api/v1/restaurants")
```

**What this proves:** Authentication is applied intentionally per route. Financial and identity mutations require proof of identity (`401`), but reading restaurant listings does not require an account (`200`).

### Step 2: Log in and place an order with a valid JWT
Log in as Priya to receive a signed RS256 token, then submit the order with the bearer token:

> 💡 **Deep Dive Reference:** For full sequence diagrams, RS256/JWKS mechanics, and atomic CAS refresh-token rotation with replay detection, see [`docs/learn/token-and-refresh-flow.md`](../learn/token-and-refresh-flow.md) and [`docs/diagrams/day-10-auth.md`](../diagrams/day-10-auth.md).

```bash
TOKEN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" \
  -d '{"email":"priya@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')

curl -s -o /dev/null -w "POST /orders (with token): %{http_code}\n" -X POST http://localhost:5224/api/v1/orders \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY"   # 201
```
```powershell
$loginBody = @{ email = "priya@tadka.test"; password = "Password123!" } | ConvertTo-Json
$TOKEN = (Invoke-RestMethod -Uri "http://localhost:5224/api/v1/auth/login" -Method Post -ContentType "application/json" -Body $loginBody).accessToken
$headers = @{ Authorization = "Bearer $TOKEN" }

Write-Host "POST /orders (with token):" (Get-StatusCode -Uri "http://localhost:5224/api/v1/orders" -Method Post -Headers $headers -Body $BODY)   # 201
```

**Key Architectural Security Property:** Notice the payload contains `"customerId":"c1b2c3d4-0001..."`. Even if a malicious user alters that JSON body field to another customer's ID, `OrdersController` ignores body-supplied customer IDs for non-admins and extracts the authenticated caller's identity strictly from the verified token's `sub` claim (`User.UserId()`). Spoofing identity in the request body is impossible.

### How This Is Actually Implemented

Full walkthrough with code — password verification, JWT construction, and where the RSA signing key lives — is in [section 10](#10-how-the-access-token-is-actually-generated-rs256-walkthrough) at the end of this runbook. The short version: [`AuthController.Login`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Auth/AuthController.cs) verifies the password hash, then `TokenService.CreateAccessToken` signs a JWT with an in-memory RSA key. The identity-spoofing defence itself is in [`OrdersController.Create`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Controllers/OrdersController.cs):
```csharp
var effectiveCustomerId = User.IsAdmin() ? request.CustomerId : (User.UserId() ?? request.CustomerId);
```
`User.UserId()` reads the `sub` claim off the *verified* token ([`Auth.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Auth/Auth.cs)) — a non-admin caller's own identity always wins over whatever `customerId` they typed into the JSON body.

### Option Space: The Same Idea in Other Stacks

| | Java (Spring Security) | Node (Express) | Go |
|---|---|---|---|
| **Issue an RS256 JWT** | `Jwts.builder().setClaims(claims).signWith(privateKey, SignatureAlgorithm.RS256).compact()` (jjwt), or Nimbus JOSE+JWT for more control over the header | `jsonwebtoken`: `jwt.sign(payload, privateKey, { algorithm: "RS256" })` | `golang-jwt/jwt`: `jwt.NewWithClaims(jwt.SigningMethodRS256, claims).SignedString(privateKey)` |
| **Reject anonymous writes** | `@PreAuthorize("isAuthenticated()")` or a global `SecurityFilterChain` rule per path pattern | `express-jwt` / `passport-jwt` middleware mounted on the write routes only | A middleware wrapping `http.Handler`, checking the parsed claims context value before calling the next handler |
| **Trust the token over the body** | Same principle — pull the principal from `SecurityContextHolder`, never from the request DTO, for anything security-relevant | Same — read `req.user.sub` (set by the JWT middleware), never `req.body.customerId`, for a non-admin | Same — read the claim from the request context set by the auth middleware, never the decoded JSON body |

What doesn't change across any of these: the *rule* — never trust a client-supplied identity field once you have a verified token — is a security principle, not a framework feature. Every stack above has a different one-liner for verifying a signature; none of them has a one-liner for "and also don't let the body lie about who's asking."

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
```powershell
# Place an order and extract its generated ID:
$ORDER = (Invoke-RestMethod -Uri "http://localhost:5224/api/v1/orders" -Method Post -Headers $headers -ContentType "application/json" -Body $BODY).id

# Priya reads her own order → 200 OK:
Write-Host "Priya reads her order:" (Get-StatusCode -Uri "http://localhost:5224/api/v1/orders/$ORDER" -Headers $headers)
```

### Step 2: Rahul (a different customer) tries to read Priya's order
Log in as Rahul and attempt to read Priya's order ID:

```bash
RAHUL=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"rahul@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')

# Rahul tries to read Priya's order → 403 Forbidden:
curl -s -o /dev/null -w "Rahul reads Priya's order: %{http_code}\n" http://localhost:5224/api/v1/orders/$ORDER -H "Authorization: Bearer $RAHUL"
```
```powershell
$rahulLogin = @{ email = "rahul@tadka.test"; password = "Password123!" } | ConvertTo-Json
$RAHUL = (Invoke-RestMethod -Uri "http://localhost:5224/api/v1/auth/login" -Method Post -ContentType "application/json" -Body $rahulLogin).accessToken
$rahulHeaders = @{ Authorization = "Bearer $RAHUL" }

# Rahul tries to read Priya's order → 403 Forbidden:
Write-Host "Rahul reads Priya's order:" (Get-StatusCode -Uri "http://localhost:5224/api/v1/orders/$ORDER" -Headers $rahulHeaders)
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
```powershell
$o1Login = @{ email = "owner1@tadka.test"; password = "Password123!" } | ConvertTo-Json
$O1 = (Invoke-RestMethod -Uri "http://localhost:5224/api/v1/auth/login" -Method Post -ContentType "application/json" -Body $o1Login).accessToken
$o1Headers = @{ Authorization = "Bearer $O1" }
$patchBody = @{ price = @{ amount = 1 } } | ConvertTo-Json

# Edit Truffles dish as Owner 1 → 403 Forbidden:
Write-Host "owner1 edits OTHER restaurant menu:" (Get-StatusCode -Uri "http://localhost:5224/api/v1/restaurants/a1b2c3d4-0002-4000-8000-000000000002/menu/b1b2c3d4-0002-4000-8000-000000000002" -Method Patch -Headers $o1Headers -Body $patchBody)
```

> **The 401 vs. 403 Rule:**
> - **401 Unauthorized:** Authentication failed. "Who are you?" (Missing, expired, or invalid token).
> - **403 Forbidden:** Authorization failed. "We know who you are, but you cannot touch this resource." (Wrong role or not the owner). `Admin` role bypasses ownership.

### How This Is Actually Implemented

The order-ownership check, [`OrdersController.GetById`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Controllers/OrdersController.cs):
```csharp
if (!User.IsAdmin() && order.CustomerId != User.UserId())
    return Forbid();
```
The restaurant/menu-ownership check is the same shape, factored into a private helper, [`RestaurantsController.OwnsOrAdmin`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Controllers/RestaurantsController.cs):
```csharp
private bool OwnsOrAdmin(Guid restaurantId) => User.IsAdmin() || User.OwnedRestaurantId() == restaurantId;
...
if (!OwnsOrAdmin(id)) return Forbid();
```
Both are plain inline `if` statements, not a policy engine, not a permissions table, not a custom `[Authorize]` attribute. At four roles and one ownership rule per resource type, that's the whole mechanism — no framework machinery sits between the `[Authorize]` on the controller (which only checks the role) and this second, explicit ownership check underneath it.

### Option Space: The Same Idea in Other Stacks

| | Java (Spring Security) | Node | Go |
|---|---|---|---|
| **RBAC** | `@PreAuthorize("hasRole('Customer')")` — declarative, evaluated by an AOP proxy before the method runs | Express middleware reading `req.user.role`, or NestJS's `@Roles()` guard | A middleware checking a claim off the request context; no attribute/decorator convention exists, so it's written out explicitly |
| **Resource ownership** | A `PermissionEvaluator` bean wired into `@PreAuthorize("hasPermission(#id, 'Restaurant', 'edit')")` — real machinery, worth it once the rule count grows past a handful of inline checks | **CASL** expresses role *and* ownership as one declarative rule: `can('edit', 'Restaurant', { ownerId: user.id })` — a genuinely different shape from two separate checks, not a 1:1 port | No ownership-rule library in common use; the idiomatic Go answer is the same inline comparison this repo uses, just written in Go |

**What doesn't change:** the 401-vs-403 distinction is HTTP semantics, not framework behaviour — "who are you" failures are always 401, "you're known but not allowed" failures are always 403, in every one of these stacks. Whether the ownership check is a Spring `PermissionEvaluator`, a CASL ability, or a one-line inline comparison is a *complexity* trade-off (worth it once the rule count grows), not a *correctness* one — this repo's inline check at four roles is arguably the more honest code, per the comment already in `RestaurantsController.cs`.

---

## 4. Demo 3 — Defense in Depth: Per-Service Validation (ADR-031)

A classic microservice anti-pattern is **Gateway-Only Authentication**: the gateway verifies the JWT, strips it, and forwards a plain header like `X-User-Id: 123` to internal services over the internal network. If an attacker breaches the network or calls the service port directly, they gain complete unauthorized access.

Tadka enforces **Per-Service Validation**: `Tadka.Payment.Api` on port `:5240` independently validates the JWT signature via JWKS (`/.well-known/jwks.json`) from `Tadka.Api`, without relying on a shared secret.

### Step 1: Hit Payment HTTP endpoint with no token
```bash
curl -s -o /dev/null -w "Payment GET (no token): %{http_code}\n" http://localhost:5240/payments/$ORDER   # 401
```
```powershell
Write-Host "Payment GET (no token):" (Get-StatusCode -Uri "http://localhost:5240/payments/$ORDER")   # 401
```

### Step 2: Hit Payment HTTP endpoint with Priya's valid token
```bash
curl -s -o /dev/null -w "Payment GET (with token): %{http_code}\n" http://localhost:5240/payments/$ORDER -H "Authorization: Bearer $TOKEN"   # 200 or 404
```
```powershell
Write-Host "Payment GET (with token):" (Get-StatusCode -Uri "http://localhost:5240/payments/$ORDER" -Headers $headers)   # 200 or 404
```

**What this proves:** Even if an internal service port is directly exposed, it cannot be called anonymously. The Payment service validates the cryptographic signature against the public JWKS endpoint independently. Furthermore, Payment checks that the caller either owns the order or has the `Admin` role (`403` if Rahul attempts to query Priya's payment record).

### How This Is Actually Implemented

Payment.Api never holds a copy of the monolith's signing key — it fetches the *public* half over HTTP and verifies signatures with that, caching it in memory. [`JwksClient.ResolveAsync`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Payment.Api/Auth/JwksClient.cs):
```csharp
public async Task<SecurityKey?> ResolveAsync(string kid, CancellationToken ct)
{
    await EnsureFreshAsync(force: false, ct);
    if (TryGet(kid, out var key)) return key;
    await EnsureFreshAsync(force: true, ct);   // kid not found — maybe just rotated, force one refetch
    return TryGet(kid, out key) ? key : null;
}
```
This is wired into ASP.NET's own JWT validation pipeline in [`Program.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Payment.Api/Program.cs):
```csharp
options.TokenValidationParameters.IssuerSigningKeyResolver = (_, _, kid, _) =>
{
    var key = jwksClient.ResolveAsync(kid, CancellationToken.None).GetAwaiter().GetResult();
    return key is null ? [] : new SecurityKey[] { key };
};
```
So the framework calls into `JwksClient` every time it needs to verify a signature — Payment.Api never trusts a header, never shares a secret with the monolith, and would keep working even if it were the *only* thing standing between an attacker and the internal network.

The ownership check itself, right next to the query, in a minimal API endpoint rather than a controller — [`Program.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Payment.Api/Program.cs):
```csharp
app.MapGet("/payments/{orderId:guid}", async (Guid orderId, PaymentDbContext db, HttpContext http) =>
{
    var p = await db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.OrderId == orderId);
    if (p is null) return Results.NotFound();
    var isSelfOrAdmin = http.User.IsAdmin() || (p.CustomerId is { } ownerId && ownerId == http.User.UserId());
    if (!isSelfOrAdmin) return Results.Forbid();
    return Results.Ok(...);
}).RequireAuthorization();
```
A payment with no `CustomerId` on record is readable by Admin only — there's no owner to compare against, so the safe default is "no non-admin may read this," not "everyone may."

### Option Space: The Same Idea in Other Stacks

| | Java (Spring Security) | Node | Go |
|---|---|---|---|
| **Verify against a remote JWKS, with caching** | `NimbusJwtDecoder.withJwkSetUri(uri).build()` — one line; Spring's own decoder fetches, caches, and refreshes keys automatically, no hand-rolled client needed | `jwks-rsa` + `express-jwt`: `jwksClient({ jwksUri })` supplies a `getKey` callback with the same caching behaviour built in | `MicahParks/keyfunc`: `keyfunc.Get(jwksUri)` returns a ready-made `jwt.Keyfunc`, same idea, less DIY than this repo's hand-rolled `JwksClient` |
| **Per-service ownership check** | Same shape — a plain comparison inside the controller method, or a `PermissionEvaluator` if the rule count justifies it | Same — an inline comparison inside the route handler | Same — an inline comparison inside the handler function |

**What's genuinely different here, worth naming:** Java's and Node's ecosystems both have a mature, one-line JWKS-fetching-and-caching feature built into their mainstream security libraries; this repo's `JwksClient` (~90 lines) exists because .NET's `Microsoft.IdentityModel` gives you the low-level pieces (`IssuerSigningKeyResolver`, `RsaSecurityKey`) but not a ready-made "fetch and cache a JWKS document" client the way Spring or `jwks-rsa` do. Hand-rolling it here is deliberate — it's the whole point of showing the mechanics — but a real .NET production system would likely reach for a library rather than reimplement this.

---

## 5. Demo 4 — PII: Response Masking & Right-To-Be-Forgotten (ADR-032)

Authentication controls access to APIs. **PII Protection** governs how sensitive data is exposed and retained. Even authorized users must not see raw personal data of other users, and customers have a legal right to erasure (GDPR / DPDP).

### Step 1: Inspect response masking for non-owners
When Rahul queries Priya's user profile, the API redacts email and phone numbers before returning the response:

```bash
curl -s http://localhost:5224/api/v1/users/c1b2c3d4-0001-4000-8000-000000000001 -H "Authorization: Bearer $RAHUL"
# Output: {"id":"c1b2c3d4-0001...","name":"Priya Sharma","email":"p***@tadka.test","phone":"+91••••••••01"}
```
```powershell
Invoke-RestMethod -Uri "http://localhost:5224/api/v1/users/c1b2c3d4-0001-4000-8000-000000000001" -Headers $rahulHeaders
# Output: name Priya Sharma, email p***@tadka.test, phone +91••••••••01
```

Notice this is a `200 OK`, not a `403`. Rahul is allowed to query the delivery recipient, but sensitive contact information is masked in transit.

### Step 2: GDPR Right-to-be-Forgotten (Anonymization Tombstone)
Execute Priya's deletion request:

```bash
curl -s -o /dev/null -w "forget: %{http_code}\n" -X POST http://localhost:5224/api/v1/users/c1b2c3d4-0001-4000-8000-000000000001/forget -H "Authorization: Bearer $TOKEN"   # 204
```
```powershell
Write-Host "forget:" (Get-StatusCode -Uri "http://localhost:5224/api/v1/users/c1b2c3d4-0001-4000-8000-000000000001/forget" -Method Post -Headers $headers)   # 204
```

Verify the database row in PostgreSQL:

```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \"Name\",\"Email\" FROM identity.users WHERE \"Id\"='c1b2c3d4-0001-4000-8000-000000000001';"
# Name: [deleted]
# Email: deleted+c1b2c3d4000140008000000000000001@tadka.invalid
```
```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \`"Name\`",\`"Email\`" FROM identity.users WHERE \`"Id\`"='c1b2c3d4-0001-4000-8000-000000000001';"
# Name: [deleted]
# Email: deleted+c1b2c3d4000140008000000000000001@tadka.invalid
```
Every `psql` command in this runbook needs double-quoted column names, because the schema is case-sensitive EF Core generated SQL. In bash, `\"` works fine. In PowerShell, plain `\"` gets stripped before it reaches `docker.exe`, and the query fails with "column does not exist". The sequence that survives is a backslash followed by a backtick-quote, written above as `` \`" ``. Every PowerShell `psql` command in this runbook uses it.

**Why Anonymize instead of Hard Delete?** A hard `DELETE FROM users` would cascade-delete or orphan foreign keys in `orders`, destroying financial audit trails and historical reporting. Anonymization overwrites PII with placeholder tombstones while preserving referential integrity.

> **Production Reality (Event Sourcing / Kafka):** Past Kafka events already published to topics like `order-placed` cannot be retroactively edited. Architectural mitigations include:
> 1. **PII Minimization:** Do not include raw phone numbers or physical addresses in Kafka event schemas (publish IDs only).
> 2. **Crypto-Shredding:** Encrypt PII in event payloads with a per-user key; deleting the user's key renders historical event payloads unreadable.

### How This Is Actually Implemented

Both masking and RTBF live in [`UsersController.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Controllers/UsersController.cs). Masking is a straight ternary at response-build time, not a database-level transform:
```csharp
var isSelfOrAdmin = User.IsAdmin() || User.UserId() == id;
return Ok(new
{
    user.Id, user.Name, user.Role,
    Email = isSelfOrAdmin ? user.Email : PiiMasker.Email(user.Email),
    Phone = isSelfOrAdmin ? user.Phone : PiiMasker.Phone(user.Phone)
});
```
The masking functions themselves, [`PiiMasker.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Infrastructure/Pii/PiiMasker.cs):
```csharp
public static string Phone(string? phone) =>
    phone.Length <= 5 ? "••" : phone[..3] + new string('•', phone.Length - 5) + phone[^2..];
public static string Email(string? email) =>
    (parts[0].Length <= 1 ? parts[0] : parts[0][..1] + "***") + "@" + parts[1];
```
Notice the same owner-or-admin check appears here as in Demo 2 and Demo 3 — this repo never centralizes it into a shared policy class; each controller writes it inline, on purpose, at this rule count.

`/forget` is a straight anonymize-in-place, not a delete:
```csharp
if (!User.IsAdmin() && User.UserId() != id) return Forbid();
user.Name = "[deleted]";
user.Email = $"deleted+{id:N}@tadka.invalid";
user.Phone = "";
user.PasswordHash = "";
user.SavedAddresses.Clear();
await db.SaveChangesAsync();
```
The row still exists — every foreign key from `orders` to this user id still resolves — but nothing personally identifying remains in it.

### Option Space: The Same Idea in Other Stacks

| | Java (Jackson) | Node | Go |
|---|---|---|---|
| **Response masking** | A custom `@JsonSerialize(using = MaskingSerializer.class)` annotation on the DTO field — masking happens automatically at serialization time, the controller code never sees it | `class-transformer`'s `@Transform()` decorator, or a custom Nest.js interceptor — same "automatic at serialization" idea | No annotation/decorator convention for this in idiomatic Go — the common answer is exactly this repo's approach: build the response DTO by hand and mask explicitly per field |
| **RTBF / anonymize** | Same shape — a service method setting placeholder values and saving, whatever the ORM (JPA/Hibernate) | Same — an async function setting placeholder fields via whatever ORM (Prisma/TypeORM) and calling save | Same — a handler function updating the row's fields directly via `database/sql` or an ORM like GORM |

**Worth naming:** Java's and Node's annotation/decorator-based masking is arguably *less* visible at the call site than this repo's explicit ternary — a reviewer scanning `UsersController.Get` sees exactly when masking applies, where a Jackson annotation buried on a DTO field three files away doesn't announce itself the same way. Neither approach is more "correct"; the explicit version trades a few extra lines for something a new team member can read top-to-bottom without knowing the serialization framework's conventions.

---

## 6. Deep-Dive: Column-Level Encryption & Card Tokenization (ADR-052 / ADR-053)

Masking protects data in transit. Column-level encryption protects data **at rest** against compromised database backups, read-replica leaks, or rogue DBA access.

### Step 1: Direct database inspection shows ciphertext
Query PostgreSQL directly to observe how `Phone` is stored on disk:

```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \"Name\", \"Phone\" FROM identity.users;"
```
```powershell
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \`"Name\`", \`"Phone\`" FROM identity.users;"
```

The database stores random AES-GCM ciphertext blobs (e.g. `4yIKUWKg4386lm9PVjfcsx...`).

Now read the profile via the API as the authenticated owner. Use Rahul, not Priya — Demo 4's `/forget` step already anonymized Priya's row, so her phone is empty by this point in the runbook; Rahul's record is still intact:

```bash
curl -s http://localhost:5224/api/v1/users/c1b2c3d4-0002-4000-8000-000000000002 -H "Authorization: Bearer $RAHUL"
# Decrypted transparently: "phone":"+919876500002"
```
```powershell
Invoke-RestMethod -Uri "http://localhost:5224/api/v1/users/c1b2c3d4-0002-4000-8000-000000000002" -Headers $rahulHeaders
# Decrypted transparently: phone +919876500002
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
```powershell
$adminLogin = @{ email = "admin@tadka.test"; password = "Password123!" } | ConvertTo-Json
$ADMIN = (Invoke-RestMethod -Uri "http://localhost:5224/api/v1/auth/login" -Method Post -ContentType "application/json" -Body $adminLogin).accessToken
$adminHeaders = @{ Authorization = "Bearer $ADMIN" }
$chargeBody = @{ orderId = "11111111-1111-4111-8111-111111111111"; amount = 299.00; currency = "INR"; cardNumber = "4111 1111 1111 1111" } | ConvertTo-Json

Invoke-RestMethod -Uri "http://localhost:5240/payments/charge" -Method Post -Headers $adminHeaders -ContentType "application/json" -Body $chargeBody
```

Inspect the `payment.payments` table in PostgreSQL:

```bash
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \"OrderId\",\"CardToken\",\"CardLast4\" FROM payment.payments WHERE \"OrderId\"='11111111-1111-4111-8111-111111111111';"
```
```powershell
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \`"OrderId\`",\`"CardToken\`",\`"CardLast4\`" FROM payment.payments WHERE \`"OrderId\`"='11111111-1111-4111-8111-111111111111';"
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
```powershell
$env:Payment__LogRawCardNumber="true"; dotnet run --project src/Tadka.Payment.Api
```

Triggering a charge now prints the raw card number to standard out. This illustrates the catastrophic defect of accidental debug logging in production (which violates PCI-DSS Requirement 3). The default `false` lever prevents card numbers from ever entering log aggregation pipelines.

### How §6 Is Actually Implemented

**Encryption (Phone) — reversible, AES-256-GCM.** The cipher itself, [`FieldCipher.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Infrastructure/Security/FieldCipher.cs):
```csharp
public static string Encrypt(string plaintext)
{
    var nonce = RandomNumberGenerator.GetBytes(NonceSize);   // 12 random bytes, fresh every call
    ...
    using var aes = new AesGcm(_key, TagSize);
    aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
    // stored as: nonce (12B) + tag (16B) + ciphertext, base64
}
```
A fresh random nonce on every call is exactly why the same phone number produces different ciphertext each time it's saved — the trade-off named above.

It hooks into EF Core as a **value converter**, [`UserConfiguration.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Data/Configurations/UserConfiguration.cs):
```csharp
var phone = builder.Property(u => u.Phone).HasMaxLength(250);
if (FieldCipher.Enabled)
    phone.HasConversion(v => FieldCipher.Encrypt(v), v => FieldCipher.Decrypt(v));
```
EF calls `Encrypt` on every `SaveChanges()` and `Decrypt` the moment a row materializes back into a `User` object — nothing in `UsersController` or anywhere else knows encryption is happening; `Phone` is just a `string` everywhere except this one config file. The key itself is wired up once at startup in `Program.cs` via `FieldCipher.Configure(Demo:EncryptPiiAtRest, Demo:EncryptionKey)`, before the EF model builds — which is exactly why flipping `Demo:EncryptPiiAtRest` needs a fresh volume: the flag is read once, at model-build time, not per request.

**Tokenization (card number) — one-way, keyed HMAC-SHA-256.** [`CardTokenizer.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Payment.Api/Infrastructure/CardTokenizer.cs):
```csharp
var hash = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(digitsOnly));
var token = "TOK-" + Convert.ToHexString(hash)[..16];
var last4 = digitsOnly[^4..];
```
No decrypt function exists anywhere in this class — this is a one-way hash, not a cipher. It must be *keyed*, not a bare hash: a card's real entropy once the public BIN and the already-visible last 4 digits are accounted for is only around 6 digits, roughly 100,000 SHA-256 guesses per candidate — trivial to brute-force from a leaked token table unless the hash is keyed with a secret only this service holds.

It's called from [`PaymentService.ChargeAsync`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Payment.Api/PaymentService.cs):
```csharp
if (!string.IsNullOrWhiteSpace(cardNumber))
{
    (cardToken, cardLast4) = CardTokenizer.Tokenize(cardNumber);
    if (options.CurrentValue.LogRawCardNumber)
        logger.LogWarning("DEMO LEVER (LogRawCardNumber): raw card number {CardNumber} ...", cardNumber, orderId);
}
```
The raw `cardNumber` parameter only exists in this one method's local scope. The `Payment` entity actually saved to the database never has a `CardNumber` field, only `CardToken`/`CardLast4` — there's no column that *could* leak a PAN even by accident. `LogRawCardNumber` is a deliberately built anti-pattern: the one place in the codebase where the raw number touches a logger, gated behind a flag that defaults to `false`.

### Option Space: The Same Idea in Other Stacks

| | Java (Hibernate) | Node | Go |
|---|---|---|---|
| **Transparent column encryption** | A JPA `AttributeConverter<String,String>` — same mechanism as EF Core's value converter, called automatically on persist/load; or Hibernate's `@ColumnTransformer` for a database-side function | No transparent-conversion feature the way EF/Hibernate have — a Prisma middleware or a repository-layer wrapper does the encrypt/decrypt by hand around every read/write | No ORM-level converter convention in idiomatic Go — encrypt/decrypt calls are usually explicit in the repository function, same shape as this repo's own `FieldCipher` calls if it weren't wired through `HasConversion` |
| **Keyed one-way tokenization** | `Mac.getInstance("HmacSHA256")`, `mac.init(new SecretKeySpec(key, "HmacSHA256"))`, `mac.doFinal(cardBytes)` — same primitive, more ceremony to initialize | `crypto.createHmac('sha256', key).update(cardNumber).digest('hex')` — a one-liner, the simplest of the three | `hmac.New(sha256.New, key)`, `mac.Write(cardBytes)`, `mac.Sum(nil)` — same shape as C#'s `HMACSHA256.HashData` |

**What doesn't change:** the *reason* card tokenization is one-way while phone encryption is reversible is a business requirement (support needs to call the customer back; nobody ever needs the raw PAN again once a charge succeeds), not a language or library constraint — every stack above could implement either direction for either field. The asymmetry is a deliberate choice in this codebase, not something HMAC vs AES-GCM forces on you.

---

## 7. Run the Tests

Execute the automated test suite across both services (identical either shell):

```
dotnet test
```

### Expected Output: **70/70 passed, 0 failed**
- **`Tadka.Payment.Api.Tests` (18 passed):** Tests per-service 401 validation, JWKS public key resolution, card tokenization digests (`CardTokenizerTests`), and idempotency gates.
- **`Tadka.Api.Tests` (52 passed):** Tests JWT issuance, password hashing, RBAC + resource ownership enforcement (`OrderTrackingAuthorizationTests`), AES-GCM encryption round-trips (`FieldCipherTests`), login rate-limiting, and atomic refresh-token CAS rotation (`RefreshTokenServiceConcurrencyTests`).

---

## 8. Wiring Reference & Cross-Stack Architecture

### Where the code lives in Tadka:
- **JWT Issuance & Signing:** [`src/Tadka.Api/Auth/TokenService.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Auth/TokenService.cs), [`Jwks.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Auth/Jwks.cs), [`SigningKeyStore.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Auth/SigningKeyStore.cs).
- **Refresh Token Lifecycle & CAS Rotation:** [`src/Tadka.Api/Auth/RefreshTokenService.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Auth/RefreshTokenService.cs).
- **Per-Service Validation:** [`src/Tadka.Payment.Api/Auth/JwksClient.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Payment.Api/Auth/JwksClient.cs) and `Program.cs` (`AddJwtBearer` with dynamic key resolver).
- **Ownership Gates:** Inline checks in [`OrdersController.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Controllers/OrdersController.cs) and [`RestaurantsController.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Controllers/RestaurantsController.cs) (`OwnsOrAdmin`).
- **PII Masking & RTBF:** [`UsersController.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Controllers/UsersController.cs), [`FieldCipher.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Infrastructure/Security/FieldCipher.cs).
- **Payment Tokenization:** [`CardTokenizer.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Payment.Api/Infrastructure/CardTokenizer.cs).

### In-Depth Visual Guides:
- **Token Generation & Refresh-Token Flow:** [`docs/learn/token-and-refresh-flow.md`](../learn/token-and-refresh-flow.md) — Comprehensive guide covering dual-token design, RS256/JWKS key distribution, atomic CAS rotation, and replay detection sequence diagrams.
- **Authentication & Authorization Diagrams:** [`docs/diagrams/day-10-auth.md`](../diagrams/day-10-auth.md) — Sequence diagrams and 401 vs 403 decision trees.

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

## 9. Bonus Showcase: Enterprise IdP Integration (Keycloak)

### Architectural Context: How do we change Tadka to use an Enterprise IdP?
A common question students ask: *"How do we swap our hand-rolled auth for an enterprise IAM platform like Keycloak, Okta, or Auth0 in production?"*

Because `Tadka.Payment.Api` implements **defense-in-depth and dynamic JWKS public key resolution** (ADR-049), **the downstream microservice requires ZERO C# code changes!**

| Concern | Monolith (Issuing Side) | Payment API (Verifying Side) |
|---|---|---|
| **What changes?** | In production, delete `TokenService`/`AuthController` and delegate user login to Keycloak's OAuth2 login page | **Pure configuration change**: point `Jwt:Issuer`, `Jwt:JwksBaseUrl`, and `Jwt:JwksPath` to Keycloak |
| **Code changes needed?** | Switch frontend to OIDC redirect | **0 lines of C# code** (handled by `appsettings.Keycloak.json` / launch profile) |

> 📖 **Full Deep-Dive:** See [`docs/learn/keycloak-integration-showcase.md`](../learn/keycloak-integration-showcase.md) for sequence diagrams and Keycloak admin setup details.

---

### Step 1: Start the Pre-Seeded Keycloak Container
Start Keycloak 24+ with an embedded database and pre-imported `tadka` realm (identical either shell):

```
docker compose --profile auth-prod up -d keycloak
```
Verify Keycloak is ready (~15s):
```bash
curl -s http://localhost:8080/realms/tadka/.well-known/openid-configuration | grep "jwks_uri"
# -> "jwks_uri":"http://localhost:8080/realms/tadka/protocol/openid-connect/certs"
```
```powershell
(Invoke-RestMethod -Uri "http://localhost:8080/realms/tadka/.well-known/openid-configuration").jwks_uri
# -> http://localhost:8080/realms/tadka/protocol/openid-connect/certs
```
*(Keycloak Admin Console is available at `http://localhost:8080` with credentials `admin`/`admin`)*

---

### Step 2: Start Tadka.Payment.Api with the Keycloak Profile
Launch the Payment service configured to validate tokens against Keycloak (identical either shell):

```
dotnet run --project src/Tadka.Payment.Api --launch-profile Keycloak
```

Notice the startup log:
```text
info: Microsoft.Hosting.Lifetime[0]
      Hosting environment: Keycloak
```

---

### Step 3: Run the 4-Step Keycloak Demo

Open a new terminal to run the demo commands:

#### 1. Unauthenticated request → 401 Unauthorized
```bash
curl -s -o /dev/null -w "Payment (no token): %{http_code}\n" http://localhost:5240/payments/11111111-1111-4111-8111-111111111111
# Expected output: 401
```
```powershell
Write-Host "Payment (no token):" (Get-StatusCode -Uri "http://localhost:5240/payments/11111111-1111-4111-8111-111111111111")
# Expected output: 401
```

#### 2. Request real OIDC tokens from Keycloak
Request OAuth2 tokens using the pre-seeded accounts (`Password123!`):

```bash
# Obtain Priya's token (Customer):
PRIYA_TOKEN=$(curl -s -X POST http://localhost:8080/realms/tadka/protocol/openid-connect/token \
  -d "client_id=tadka-api" -d "username=priya@tadka.test" -d "password=Password123!" -d "grant_type=password" | sed -E 's/.*"access_token":"([^"]+)".*/\1/')

# Obtain Admin's token (Admin):
ADMIN_TOKEN=$(curl -s -X POST http://localhost:8080/realms/tadka/protocol/openid-connect/token \
  -d "client_id=tadka-api" -d "username=admin@tadka.test" -d "password=Password123!" -d "grant_type=password" | sed -E 's/.*"access_token":"([^"]+)".*/\1/')
```
```powershell
$priyaRes = Invoke-RestMethod -Uri "http://localhost:8080/realms/tadka/protocol/openid-connect/token" -Method Post -Body @{ client_id = "tadka-api"; username = "priya@tadka.test"; password = "Password123!"; grant_type = "password" }; $PRIYA_TOKEN = $priyaRes.access_token

$adminRes = Invoke-RestMethod -Uri "http://localhost:8080/realms/tadka/protocol/openid-connect/token" -Method Post -Body @{ client_id = "tadka-api"; username = "admin@tadka.test"; password = "Password123!"; grant_type = "password" }; $ADMIN_TOKEN = $adminRes.access_token
```

#### 3. Resource Ownership Defense → 403 Forbidden
Priya sends a valid Keycloak token, but attempts to read an order belonging to another customer (`11111111-...`):

```bash
curl -s -o /dev/null -w "Priya reads other order: %{http_code}\n" http://localhost:5240/payments/11111111-1111-4111-8111-111111111111 -H "Authorization: Bearer $PRIYA_TOKEN"
# Expected output: 403
```
```powershell
Write-Host "Priya reads other order:" (Get-StatusCode -Uri "http://localhost:5240/payments/11111111-1111-4111-8111-111111111111" -Headers @{ Authorization = "Bearer $PRIYA_TOKEN" })
# Expected output: 403
```
**What this proves:** Authentication passed! Keycloak's RS256 signature was verified by `JwksClient`. But Tadka's inline ownership check (`CustomerId == User.UserId()`) barred access. **Identity Providers assert identity, but your application code must protect its own resources.**

#### 4. Admin Bypass & Customer Own Order → 200 OK
1. Admin reads any order (`200 OK`):
```bash
curl -s -o /dev/null -w "Admin reads order: %{http_code}\n" http://localhost:5240/payments/11111111-1111-4111-8111-111111111111 -H "Authorization: Bearer $ADMIN_TOKEN"
# Expected output: 200
```
```powershell
Write-Host "Admin reads order:" (Get-StatusCode -Uri "http://localhost:5240/payments/11111111-1111-4111-8111-111111111111" -Headers @{ Authorization = "Bearer $ADMIN_TOKEN" })
# Expected output: 200
```

2. Admin charges a new order for Priya:
```bash
curl -s -X POST http://localhost:5240/payments/charge -H "Content-Type: application/json" -H "Authorization: Bearer $ADMIN_TOKEN" \
  -d '{"orderId":"22222222-2222-4222-8222-222222222222","amount":499.00,"currency":"INR","cardNumber":"4111 1111 1111 1111","customerId":"c1b2c3d4-0001-4000-8000-000000000001"}'
```
```powershell
$chargeBody2 = @{ orderId = "22222222-2222-4222-8222-222222222222"; amount = 499.00; currency = "INR"; cardNumber = "4111 1111 1111 1111"; customerId = "c1b2c3d4-0001-4000-8000-000000000001" } | ConvertTo-Json
Invoke-RestMethod -Uri "http://localhost:5240/payments/charge" -Method Post -Headers @{ Authorization = "Bearer $ADMIN_TOKEN" } -ContentType "application/json" -Body $chargeBody2
```

3. Priya reads her OWN payment (`200 OK`):
```bash
curl -s http://localhost:5240/payments/22222222-2222-4222-8222-222222222222 -H "Authorization: Bearer $PRIYA_TOKEN"
# Expected output: {"orderId":"22222222-2222-4222-8222-222222222222","status":"Completed",...}
```
```powershell
Invoke-RestMethod -Uri "http://localhost:5240/payments/22222222-2222-4222-8222-222222222222" -Headers @{ Authorization = "Bearer $PRIYA_TOKEN" }
# Expected output: orderId 22222222-2222-4222-8222-222222222222, status Completed, ...
```

---

### Step 4: Teardown
When finished demonstrating Keycloak, stop and remove **only** the Keycloak container. `docker compose down` with no service name tears down the *entire* project regardless of `--profile` — verified live, it took Postgres, Redis, Payment DB, and Kafka down too, not just Keycloak. Name the service explicitly instead (identical either shell):
```
docker compose --profile auth-prod down keycloak
```
The rest of the default stack (Postgres, Redis, Payment DB, Kafka, Kafka UI) keeps running untouched. Normal `docker compose up -d` continues to work as before, without Keycloak.

---

## 10. How the Access Token Is Actually Generated (RS256 Walkthrough)

`Invoke-RestMethod ... auth/login | ... .accessToken` looks like a black box. It isn't — every step is a few lines of code in this repo. Nothing cryptographic happens on the client side; the call is a plain HTTP POST, and `Invoke-RestMethod` just auto-parses the JSON response, which is why `.accessToken` works directly on the result.

**Step 1 — `AuthController.Login` verifies the password.** [`AuthController.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Auth/AuthController.cs):
```csharp
var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Email == email);
if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
    return Unauthorized(...);
...
return Ok(await IssueTokenPairAsync(user));
```
It looks the user up, checks the password against the stored hash (never a plaintext comparison), and on success calls `IssueTokenPairAsync`.

**Step 2 — `TokenService.CreateAccessToken` builds and signs the JWT.** [`TokenService.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Auth/TokenService.cs), the actual token-generation code:
```csharp
var claims = new List<Claim>
{
    new("sub", user.Id.ToString()),      // read later as User.UserId() by every ownership check
    new("role", user.Role.ToString()),
    new("email", user.Email),
    new("jti", Guid.NewGuid().ToString())
};

var signingKey = keys.Current;
var rsaKey = new RsaSecurityKey(signingKey.Rsa) { KeyId = signingKey.Kid };
var descriptor = new SecurityTokenDescriptor
{
    Issuer = _o.Issuer,
    Audience = _o.Audience,
    Subject = new ClaimsIdentity(claims),
    Expires = DateTime.UtcNow.AddMinutes(_o.AccessTokenMinutes),
    SigningCredentials = new SigningCredentials(rsaKey, SecurityAlgorithms.RsaSha256)
};

return new JsonWebTokenHandler().CreateToken(descriptor);
```
A JWT is three base64url chunks joined by dots: `header.payload.signature`. `CreateToken` builds the header (`{"alg":"RS256","kid":"...","typ":"JWT"}`) and the payload (`sub`/`role`/`email`/`jti`/`iss`/`aud`/`exp`) as JSON, base64url-encodes each, computes an RSA-SHA256 signature over `header.payload` using the private key, and appends that as the third chunk. That whole string is the `accessToken` the login response carries.

**Step 3 — where the RSA key comes from.** [`SigningKeyStore.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Auth/SigningKeyStore.cs):
```csharp
var next = new SigningKey { Kid = Guid.NewGuid().ToString("N"), Rsa = RSA.Create(2048), CreatedAt = DateTime.UtcNow };
```
`Tadka.Api` generates a 2048-bit RSA keypair in memory on startup. The **private half** signs tokens, right there in `TokenService`. The **public half** is published at `Tadka.Api`'s own `/.well-known/jwks.json` endpoint ([`Jwks.cs`](file:///D:/work/cohort/tadka-cohort/src/Tadka.Api/Auth/Jwks.cs)) — that public key is how `Tadka.Payment.Api`, a completely separate process that never signed anything, verifies a token's signature independently. That is the actual mechanism behind Demo 3's "per-service validation."

**Why this matters for this runbook specifically:** the key lives only in process memory (a deliberate, documented trade-off — see the comment on `SigningKeyStore`), so restarting `Tadka.Api` generates a *brand new* keypair, invalidating every token issued before the restart. This is why every demo above logs in fresh after starting the apps rather than reusing a token across sessions — an old token isn't expired, it's signed by a key that no longer exists.

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
- **PowerShell: a `psql` query says "column does not exist":** you're using plain `\"` instead of the `` \`" `` escape sequence described in Demo 4 — plain `\"` gets silently stripped before it reaches `docker.exe` in PowerShell.
- **PowerShell: `Get-StatusCode` isn't recognized:** the function is defined once in section 1 and only lives for the current terminal session. Re-paste its definition if you opened a new PowerShell window.

➡️ **Next (Day 11):** Extract the **Delivery** service (with real-time location tracking via Redis-geo) and introduce the **API Gateway** (YARP) for edge rate-limiting and reverse proxying.
