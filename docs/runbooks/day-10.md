# Day 10 — Runbook: Authentication, Authorization & PII

**Branch:** `day-10`  ·  **What changed since Day 9:** [`docs/changelog.md`](../changelog.md). **What's new:** the system was wide open; now it's secured. **JWT login** (ADR-030), **RBAC + resource-ownership** validated **per-service** (ADR-031, defense in depth — the Payment service verifies the same token), and **PII protection** (ADR-032 — log masking + GDPR right-to-be-forgotten). Same infra as Day 9.

> New here? Read [`README.md`](README.md). Windows PowerShell → `curl.exe`. Demo password for every seeded account: **`Password123!`**.

## 1. Run it

Same infra as Day 9, plus both apps — the monolith now seeds demo users on its first boot:
```bash
git checkout day-10
docker compose up -d                       # postgres + replica + redis + payment-db + kafka + kafka-ui
dotnet run --project src/Tadka.Payment.Api    # :5240
dotnet run --project src/Tadka.Api            # :5224  (seeds demo users on startup)
```
Every seeded account shares one password so the demos below don't get bogged down in credential bookkeeping: `admin@tadka.test` (Admin) · `priya@tadka.test` / `rahul@tadka.test` (Customers) · `owner1@tadka.test` (owns Meghana `a1b2c3d4-0001…`) · `owner2@tadka.test` (owns `a1b2c3d4-0002…`). The two-customer, two-owner spread is deliberate — every demo below needs a **second** identity to prove an authorization failure against, not just a first one to prove success.

## 2. Demo 1 — auth bypass is closed (ADR-030)

**The story (say this before any command):** through Day 9 there is no authentication anywhere in this system — every endpoint is callable by anyone, `POST /orders` will happily place an order for any `customerId` you type into the body. This demo closes that gap for the write path while deliberately leaving the read-only menu browse public, because a customer shouldn't need an account just to look at a menu.

Call `POST /orders` with no `Authorization` header at all — this is the exact request that worked without complaint through every prior day:
```bash
RID=a1b2c3d4-0001-4000-8000-000000000001; ITEM=b1b2c3d4-0001-4000-8000-000000000001
BODY='{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"'$RID'","items":[{"menuItemId":"'$ITEM'","quantity":1}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.9,"longitude":77.7}}'

# No token → 401 (browsing the menu is still public):
curl -s -o /dev/null -w "POST /orders (no token): %{http_code}\n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY"   # 401
curl -s -o /dev/null -w "GET /restaurants (public): %{http_code}\n" http://localhost:5224/api/v1/restaurants   # 200
```
**Outcome interpretation:** the two status codes side by side are the point of this demo — `401` on the order write and `200` on the menu read are *both* correct, from the *same* pass through the router. Authentication is applied per-endpoint, not globally: writes that spend money or touch someone's identity require a token, but browsing what a restaurant sells shouldn't need an account any more than walking past a restaurant window does.

Now log in and repeat the same write with a real token:
```bash
TOKEN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" \
  -d '{"email":"priya@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
curl -s -o /dev/null -w "POST /orders (with token): %{http_code}\n" -X POST http://localhost:5224/api/v1/orders \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY"   # 201
```
> Captured: no token **401**; login → a real JWT; with the token **201**. Your identity is the token's `sub` — `POST /orders` ignores any `customerId` in the body for non-admins.

**Why that last line matters more than it looks:** the request body above still carries `"customerId":"c1b2c3d4-0001…"` (Priya's own seeded id, by coincidence) — but the server never trusts that field for a non-admin caller. It reads `sub` off the verified, signed token instead. If you edited the body to claim a *different* customer's id, the order would still be recorded as belonging to whoever the token says you are — that's the actual security property this demo is proving, not just "a 401 turned into a 201."

## 3. Demo 2 — RBAC + resource ownership (ADR-031)

**The story (say this before any command):** a role check alone only answers "can this *kind* of user do this *kind* of thing" — it can't answer "does this *specific* user own this *specific* resource." A `Customer` can read orders in general, but only their own; a `RestaurantOwner` can edit menus in general, but only the restaurant they actually own. This demo runs three separate ownership checks to show the same pattern (role passes, ownership decides) across two different resource types.

Priya places an order with her own token and reads it back — this should just work, it's her own resource:
```bash
# Priya places an order (token from above), grab its id:
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
curl -s -o /dev/null -w "Priya reads her order: %{http_code}\n" http://localhost:5224/api/v1/orders/$ORDER -H "Authorization: Bearer $TOKEN"   # 200
```
Now log in as a second, unrelated customer and try to read Priya's order with a perfectly valid token — just the wrong identity:
```bash
# Rahul (a different customer) tries to read Priya's order → 403:
RAHUL=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"rahul@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
curl -s -o /dev/null -w "Rahul reads Priya's order: %{http_code}\n" http://localhost:5224/api/v1/orders/$ORDER -H "Authorization: Bearer $RAHUL"   # 403
```
**Outcome interpretation:** Rahul's token is completely valid — it passes authentication, it carries a real `Customer` role, the RBAC check passes cleanly. The `403` comes entirely from the *ownership* check underneath it: `order.CustomerId != rahul.sub`. This is the exact scenario a role-only system would get wrong — a coarse "is this a Customer" check would let Rahul through, because he genuinely is one.

The third case repeats the same pattern on a different resource — a restaurant owner editing a menu they don't own:
```bash
# owner1 owns Meghana; editing ANOTHER restaurant's menu → 403 (role RestaurantOwner passes, ownership fails):
O1=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"owner1@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
# Truffles menu item (owner1 owns Meghana, not Truffles — role passes, ownership fails):
curl -s -o /dev/null -w "owner1 edits OTHER restaurant menu: %{http_code}\n" -X PATCH "http://localhost:5224/api/v1/restaurants/a1b2c3d4-0002-4000-8000-000000000002/menu/b1b2c3d4-0002-4000-8000-000000000002" -H "Authorization: Bearer $O1" -H "Content-Type: application/json" -d '{"price":{"amount":1}}'   # 403
```
> **401 vs 403:** 401 = not authenticated (no/invalid token); 403 = authenticated but not allowed (wrong owner). `Admin` bypasses ownership.

Worth saying explicitly: the ownership check here (`RestaurantsController.OwnsOrAdmin`) is a plain inline check reading the `restaurantId` claim off the token and comparing it to the resource's actual owner — not a policy engine, not a permissions table. At four roles and one ownership rule, that inline check is the whole mechanism; the "wiring reference" section at the end of this runbook shows what a heavier stack (a real `PermissionEvaluator`, or a `CASL` ability) looks like once the rule count grows past what fits comfortably inline.

## 4. Demo 3 — per-service JWT validation, defense in depth (ADR-031)

**The story (say this before any command):** the previous two demos both ran against the monolith. There is no API gateway in front of this system yet, so "did the request already pass through a trust boundary" isn't a safe assumption — the Payment service is reachable directly, on its own port, by anyone who can route to it. If Payment simply trusted whatever the monolith forwarded (a plain `X-User-Id` header, say), then anything that could reach `:5240` directly — bypassing the monolith entirely — would have full access with no identity check at all. This demo proves that isn't the case: Payment verifies the same token itself, independently. Since the initial cut of this branch, Payment also stopped stopping at "is there a valid token" — it now checks *whose* order this is (the `order-placed` event carries `CustomerId`, ADR-031), the same authentication-vs-authorization split as Demo 2, just enforced a service later.

Hit Payment's own HTTP endpoint with no token, then with the same token from Demo 1:
```bash
curl -s -o /dev/null -w "payment GET (no token): %{http_code}\n" http://localhost:5240/payments/$ORDER   # 401
curl -s -o /dev/null -w "payment GET (with token): %{http_code}\n" http://localhost:5240/payments/$ORDER -H "Authorization: Bearer $TOKEN"   # 200/404
```
**Outcome interpretation:** the `401` without a token is the actual point of this demo — it means Payment is doing its **own** signature verification against the same signing key/JWKS as the monolith, not trusting a header or the fact that the request arrived at all. A `200` or `404` with the token (depending on whether that order has a payment row yet) both mean the same thing from an auth standpoint: the token was accepted and the request proceeded to the business logic, which is exactly what defense-in-depth is supposed to look like — two independent services, two independent checks, no implicit trust between them just because they're "internal." `$TOKEN` here is Priya's own token against an order *she* placed, so it also passes the ownership check underneath — swap in `$RAHUL` (Demo 2's other customer) against the same `$ORDER` and it comes back `403`, not `200`/`404`: a valid, authenticated, wrong-owner token, same shape as every other ownership check today.

## 5. Demo 4 — PII: masking + right-to-be-forgotten (ADR-032)

**The story (say this before any command):** authentication and authorization (Demos 1-3) answer *who can call what*. PII protection is a different, broader question: even an authenticated, authorized caller shouldn't see another user's raw phone number or email just because they can technically reach the endpoint — and a user who wants their data gone needs an actual, working mechanism for that, not just a policy document.

`GET /users/{id}` returns full PII to the owner or an Admin, and a **masked** version to anyone else. Rahul reading Priya's user record proves the masking, not an authorization failure — the request succeeds, the data just isn't the real data:
```bash
curl -s http://localhost:5224/api/v1/users/c1b2c3d4-0001-4000-8000-000000000001 -H "Authorization: Bearer $RAHUL"   # Rahul sees p***@tadka.test, +91••••••••01
```
**Outcome interpretation:** notice this is a `200`, not a `403` — Rahul is allowed to *look up* a user record he doesn't own (a real product need: seeing a delivery contact's masked name, say), but what comes back has had the email and phone run through a redactor before the response ever left the server. Compare this to the ownership checks in Demo 2 — those blocked the request outright; masking instead lets the request through but changes what data is in the response. Two different mechanisms, both under the PII umbrella.

Right-to-be-forgotten anonymises the row rather than deleting it — order history has to keep reconciling against a real (if now-scrubbed) user id:
```bash
curl -s -o /dev/null -w "forget: %{http_code}\n" -X POST http://localhost:5224/api/v1/users/c1b2c3d4-0001-4000-8000-000000000001/forget -H "Authorization: Bearer $TOKEN"   # 204
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \"Name\",\"Email\" FROM identity.users WHERE \"Id\"='c1b2c3d4-0001-4000-8000-000000000001';"   # [deleted], deleted+…@tadka.invalid
```
> GDPR right-to-be-forgotten → **anonymise** (not hard-delete — order history must reconcile). **This burns Priya** (`c1b2c3d4-0001…`) for the rest of the day — reset volumes after, or forget a throwaway user.

**Outcome interpretation:** `[deleted]` and a `deleted+…@tadka.invalid` placeholder email are the tombstone values — the row still exists (so every prior order Priya placed still has a valid, joinable customer id), but nothing personally identifying remains in it. **Honest limit, worth saying out loud:** events already published to Kafka or sitting in the outbox from Priya's earlier orders (Day 9) are **not** retro-scrubbed by this endpoint — they're immutable once published. The mitigation is upstream discipline, not retroactive cleanup: Tadka's events carry ids and amounts, never phone numbers or addresses, specifically so there's nothing sensitive left stranded in an old Kafka log after a `/forget` call. Anything that genuinely can't be avoided relies on crypto-shredding (deleting the encryption key, see §6) rather than trying to edit history.

## 6. Field-level encryption at rest + payment tokenization — **weekday lab** (later numbered ADR-052 / ADR-053)

> Not in the 120-minute class. ADR-030/031/032 are the day's core. On this branch the encrypt/tokenize tests still count toward **44/44**. Later branches reuse **045** for restaurant-reject; encrypt/tokenize become **052/053**. Do not teach "ADR-045" as encryption after Day 11.

**The story (say this before any command):** masking (§5) hides PII in API responses, but it does nothing for someone reading the database directly — a DBA, a leaked credential, a `pg_dump` backup. This beat closes that specific gap for one representative field, `Phone`, using AES-GCM at the column level, and separately shows why card numbers get a stricter, one-way treatment than a phone number does.

Query the phone column directly — with encryption on by default, this should be unreadable ciphertext, not a phone number:
```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \"Name\", \"Phone\" FROM identity.users;"   # ciphertext blobs, not plaintext
curl -s http://localhost:5224/api/v1/users/c1b2c3d4-0001-4000-8000-000000000001 -H "Authorization: Bearer $TOKEN" | grep -o '"phone":"[^"]*"'   # +919876500001 — decrypts correctly for the owner
```
**Outcome interpretation:** the same field is opaque at the database layer and legible through the API — that split is the entire point. Encryption-at-rest (this beat) and masking-in-transit (§5) are two independent, composable layers: the API path decrypts transparently via an EF Core value converter before the masking logic ever runs, so a request from the actual owner sees the real number, while a non-owner would see it masked *after* decryption, not because the ciphertext itself is somehow different per caller.

BREAK — turn encryption off on a fresh volume and confirm the same query now shows plaintext:
```bash
docker compose down -v && docker compose up -d
Demo__EncryptPiiAtRest=false dotnet run --project src/Tadka.Api
```
> **A gotcha worth knowing:** flipping the flag back without resetting the volume throws `FormatException` on startup — `AuthSeeder` tries to read the OLD state's values under the NEW converter. Always reset volumes when switching this lever, same discipline as any other Demo config toggle in this cohort.

**A real, permanent limitation, not just a demo caveat:** with encryption on, `Phone` is no longer queryable at the database level at all — `WHERE "Phone" = '...'` can't be pushed down to Postgres, because AES-GCM uses a fresh random nonce on every write, so the same phone number produces different ciphertext each time it's saved. Tadka doesn't query by phone today, so this cost is real but currently unrealized — worth knowing before reaching for encryption on a field you *do* need to filter by.

Card numbers get a stricter, **one-way** treatment — tokenized the instant they arrive, never logged, never persisted in recoverable form. `POST /payments/charge` is **Admin-only** now (a fix landed after the initial cut of this branch: the real charge flow runs off the `order-placed` Kafka event, in-process, never over HTTP — a customer's own token calling this directly could otherwise charge *any* orderId for *any* amount, an authorization gap this endpoint's RBAC now closes), so log in as `admin@tadka.test` for this one:
```bash
ADMIN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"admin@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
curl -s -X POST http://localhost:5240/payments/charge -H "Content-Type: application/json" -H "Authorization: Bearer $ADMIN" -d '{"orderId":"11111111-1111-4111-8111-111111111111","amount":299.00,"currency":"INR","cardNumber":"4111 1111 1111 1111"}'
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \"OrderId\",\"CardToken\",\"CardLast4\" FROM payment.payments;"   # TOK-9BBE..., 1111 — no PAN column exists
```
**Outcome interpretation:** `CardToken` and `CardLast4` are the only card-related columns that exist in `payment.payments` — there is no column anywhere in this schema that *could* hold a raw PAN, so this isn't "we chose not to store it," it's "there is nowhere to put it even by accident." Contrast this with `Phone` above: encryption is reversible by design, because Tadka legitimately needs the real phone number back to show a customer or call them. Tokenization here is deliberately one-way (a keyed HMAC-SHA-256 digest, not a cipher — a fix landed after the initial cut of this branch, when a review pointed out that an *unkeyed* SHA-256 of a card number is brute-forceable once the issuer's BIN and the stored last-4 narrow the search space) — there is no legitimate reason for this codebase to ever recover a raw card number once a charge has gone through.

Break the anti-pattern on purpose, to see what it looks like when someone adds "just one debug log line":
```bash
Payment__LogRawCardNumber=true dotnet run --project src/Tadka.Payment.Api
```
Charge again with the same request as above and watch the Payment service's own console — the raw card number now appears directly in the log output, which is exactly the PCI-DSS violation this default-off lever exists to make visible and reproducible rather than merely asserted in a document.

## 7. Run the tests

```bash
dotnet test    # 44/44 — monolith 33 (incl. 4 auth/ownership + 4 FieldCipher) + Payment 11 (incl. per-service 401 + 6 CardTokenizer).
               # Existing suites pass via a TestAuthHandler (default Admin); X-Test-NoAuth/X-Test-Auth drive 401/403.
```
The count grew from Day 9's 34/34 to 44/44: 4 new auth/ownership tests on the monolith side exercise exactly the 401-vs-403 distinction from Demo 2, 4 `FieldCipher` tests cover the encryption round-trip from §6, and on the Payment side, 1 new test covers the per-service 401 from Demo 3 plus 6 `CardTokenizer` tests cover the one-way tokenization from §6. Every pre-Day-10 test still passes unmodified because the test harness swaps in a `TestAuthHandler` that defaults to an Admin identity — so existing suites didn't need to be rewritten to carry real tokens, and the two headers (`X-Test-NoAuth`, `X-Test-Auth`) let the *new* auth-specific tests explicitly drive the 401/403 cases without standing up a real login flow in test setup.

*(Note: the counts above predate two later fixes on this branch — a RestaurantOwner ownership check on `PATCH /orders/{id}/status`, and RBAC/ownership on the Payment HTTP endpoints — each of which added its own test. `dotnet test` is the source of truth for the current total; don't hand-recite a fixed number here without re-running it.)*

## 8. Wiring reference + cross-language comparison

**Where the code lives:** monolith `Auth/*` (login/register endpoints, `TokenService`, `PasswordHasher<User>`), `[Authorize(Roles=...)]` + inline `OwnsOrAdmin`/ownership checks in each controller (Demo 2), `Infrastructure/Security/FieldCipher.cs` + `Data/Configurations/UserConfiguration.cs` (§6 encryption), the log-masking redactor + `users/{id}/forget` endpoint (Demo 4). Payment service: its own JWT bearer validation (Demo 3), `Infrastructure/CardTokenizer.cs` + `PaymentService.cs` (§6 tokenization). Full design reasoning: ADR-030 (JWT), ADR-031 (RBAC + ownership + per-service validation), ADR-032 (PII classification/masking/RTBF), ADR-045 (field encryption), ADR-046 (tokenization).

**If you'd build this same system in Java or Node instead of .NET**, the pattern is identical — a stateless signed token, a role check plus an ownership check, masked logs, an anonymise-not-delete endpoint. What changes is the tooling. Full detail in [`docs/learn/cross-stack-auth-and-pii.md`](../learn/cross-stack-auth-and-pii.md); the shape of it:

| Concern | .NET (this repo) | Java (Spring Security) | Node |
|---|---|---|---|
| Issuing/verifying JWTs | Hand-rolled `TokenService`; RS256 + JWKS as of ADR-049 | `oauth2ResourceServer().jwt()`; `NimbusJwtDecoder.withJwkSetUri(...)` is the closest one-line equivalent of this repo's hand-rolled `JwksClient` — but filter-chain *order* matters, a JWT filter registered after your security rules gives confusing 403s for what should be a 401 | `jsonwebtoken` for pure sign/verify (closest match to `TokenService.cs`); `passport-jwt` wraps it into Express middleware. `NextAuth` is built for OAuth-provider web login, not a pure API's own token issuance — the wrong tool here despite showing up in every search result |
| RBAC + ownership | Inline `[Authorize(Roles=...)]` + a plain `OwnsOrAdmin(...)` check per controller (this repo's actual, corrected-during-build approach — see ADR-031) | `@PreAuthorize("hasRole(...)")` (declarative RBAC) + a custom `PermissionEvaluator` bean for ownership — more machinery than this repo's inline check; at 4 roles + 1 ownership rule the inline version is arguably the more honest code | No built-in RBAC. **CASL** expresses role *and* ownership as one rule (`can('read','Order', {customerId: user.id})`) rather than two separate checks — a genuinely different shape, not a 1:1 port. Or hand-roll middleware matching this repo's split style directly |
| PII masking in logs | Hand-rolled redactor | Logback `TurboFilter`/custom `PatternLayout` — first-class, not a bolt-on | `pino`'s built-in `redact` option takes a list of paths (`'user.phone'`) and masks automatically — arguably cleaner out-of-the-box than most .NET setups need to hand-roll |
| Column-level encryption | EF Core value converter (`FieldCipher`, §6) | Hibernate `@ColumnTransformer` or a JPA `AttributeConverter` — the direct analogue of an EF value converter | No transparent-conversion feature the way Hibernate/EF have — Prisma middleware or a repository-layer wrapper does the encrypt/decrypt by hand |

**What doesn't change across any of these stacks:** the 401-vs-403 distinction is HTTP semantics, not framework behavior — "who are you" failures are always 401, "you're known but not allowed" failures are always 403, everywhere. The classic failure mode this whole day exists to prevent — validate only at a gateway, forward a plain `X-User-Id` header, get it forged by anything that reaches the internal network — is a network-architecture mistake, not a language bug; Demo 3's per-service validation fix applies identically no matter what the service behind the gateway is written in.

## ✅ Done when
- [ ] No token → `401`; `GET /restaurants` (public) → `200`; login → a JWT; with the token → `201`.
- [ ] A different customer reading your order → `403`; an owner editing another restaurant's menu → `403`.
- [ ] Payment service HTTP endpoint: no token → `401`, with token → `200/404`.
- [ ] `GET /users/{id}` masks PII for non-owners; `/forget` anonymises the row.
- [ ] `psql` on `identity.users` shows ciphertext for `Phone`; the API still returns the correct decrypted number to the owner.
- [ ] `Demo__EncryptPiiAtRest=false` (fresh volume) shows plaintext instead.
- [ ] A charge with `cardNumber` stores only `CardToken`/`CardLast4`; no PAN column exists in `payment.payments`.
- [ ] `Payment__LogRawCardNumber=true` makes the raw card number appear in the log (the anti-pattern, on purpose).
- [ ] `dotnet test` → **44/44**.

## Troubleshooting
- **Login returns 401 for a seeded user:** the startup `AuthSeeder` sets real hashes on first boot; if you migrated before Day 10, `docker compose down -v && docker compose up -d` then `dotnet run` to re-seed.
- **All calls 401 after adding a token:** as of the ADR-047/048/049 hardening pass there is no shared `Jwt:SigningKey` any more — signing is RS256 (ADR-049). Check that `Tadka.Api` is reachable at the URL `Tadka.Payment.Api`'s `Jwt:JwksBaseUrl` points to (`http://localhost:5224` by default) and that `curl http://localhost:5224/.well-known/jwks.json` returns a non-empty `keys` array; also give `Tadka.Payment.Api`'s JWKS cache (`Jwt:JwksCacheMinutes`, default 5) a moment if a key was *just* rotated.
- **`FormatException` on startup (`not a valid Base-64 string`):** you switched `Demo:EncryptPiiAtRest` without resetting the volume — the DB has values encoded under the OLD state. `docker compose down -v && docker compose up -d`, then restart the app.
- **A 403 where you expected a 401 (or vice versa):** re-check which layer is actually failing — a missing/malformed token is always 401 (authentication); a valid token whose owner doesn't match the resource is always 403 (authorization). If you're getting 403 with no token at all, something upstream is misconfigured to treat "no token" as "anonymous role" rather than rejecting the request outright.

➡️ Next (Day 11): extract the **Delivery** service (with real-time location tracking / Redis-geo) and front the services with the **API gateway** (YARP).
