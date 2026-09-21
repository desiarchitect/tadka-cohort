# Day 10 — Runbook: Authentication, Authorization & PII

**Branch:** `day-10`  ·  **What changed since Day 9:** [`docs/changelog.md`](../changelog.md). **What's new:** the system was wide open; now it's secured. **JWT login** (ADR-030), **RBAC + resource-ownership** validated **per-service** (ADR-031, defense in depth — the Payment service verifies the same token), and **PII protection** (ADR-032 — log masking + GDPR right-to-be-forgotten). Same infra as Day 9.

> New here? Read [`README.md`](README.md). Windows PowerShell → `curl.exe`. Demo password for every seeded account: **`Password123!`**.

## 1. Run it

```bash
git checkout day-10
docker compose up -d                       # postgres + replica + redis + payment-db + kafka + kafka-ui
dotnet run --project src/Tadka.Payment.Api    # :5240
dotnet run --project src/Tadka.Api            # :5224  (seeds demo users on startup)
```
Seeded accounts (all password `Password123!`): `admin@tadka.test` (Admin) · `priya@tadka.test` / `rahul@tadka.test` (Customers) · `owner1@tadka.test` (owns Meghana `a1b2c3d4-0001…`) · `owner2@tadka.test` (owns `a1b2c3d4-0002…`).

## 2. Demo 1 — auth bypass is closed (ADR-030)

```bash
RID=a1b2c3d4-0001-4000-8000-000000000001; ITEM=b1b2c3d4-0001-4000-8000-000000000001
BODY='{"customerId":"c1b2c3d4-0001-4000-8000-000000000001","restaurantId":"'$RID'","items":[{"menuItemId":"'$ITEM'","quantity":1}],"deliveryAddress":{"line1":"x","line2":"y","city":"Bangalore","pincode":"560066","latitude":12.9,"longitude":77.7}}'

# No token → 401 (browsing the menu is still public):
curl -s -o /dev/null -w "POST /orders (no token): %{http_code}\n" -X POST http://localhost:5224/api/v1/orders -H "Content-Type: application/json" -d "$BODY"   # 401
curl -s -o /dev/null -w "GET /restaurants (public): %{http_code}\n" http://localhost:5224/api/v1/restaurants   # 200
```
Log in, then call with the token:
```bash
TOKEN=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" \
  -d '{"email":"priya@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
curl -s -o /dev/null -w "POST /orders (with token): %{http_code}\n" -X POST http://localhost:5224/api/v1/orders \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY"   # 201
```
> Captured: no token **401**; login → a real JWT; with the token **201**. Your identity is the token's `sub` — `POST /orders` ignores any `customerId` in the body for non-admins.

## 3. Demo 2 — RBAC + resource ownership (ADR-031)

A role check isn't enough; you can only touch **your own** resources.
```bash
# Priya places an order (token from above), grab its id:
ORDER=$(curl -s -X POST http://localhost:5224/api/v1/orders -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d "$BODY" | sed -E 's/^\{"id":"([^"]+)".*/\1/')
curl -s -o /dev/null -w "Priya reads her order: %{http_code}\n" http://localhost:5224/api/v1/orders/$ORDER -H "Authorization: Bearer $TOKEN"   # 200

# Rahul (a different customer) tries to read Priya's order → 403:
RAHUL=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"rahul@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
curl -s -o /dev/null -w "Rahul reads Priya's order: %{http_code}\n" http://localhost:5224/api/v1/orders/$ORDER -H "Authorization: Bearer $RAHUL"   # 403

# owner1 owns Meghana; editing ANOTHER restaurant's menu → 403 (role RestaurantOwner passes, ownership fails):
O1=$(curl -s -X POST http://localhost:5224/api/v1/auth/login -H "Content-Type: application/json" -d '{"email":"owner1@tadka.test","password":"Password123!"}' | sed -E 's/.*"accessToken":"([^"]+)".*/\1/')
# Truffles menu item (owner1 owns Meghana, not Truffles — role passes, ownership fails):
curl -s -o /dev/null -w "owner1 edits OTHER restaurant menu: %{http_code}\n" -X PATCH "http://localhost:5224/api/v1/restaurants/a1b2c3d4-0002-4000-8000-000000000002/menu/b1b2c3d4-0002-4000-8000-000000000002" -H "Authorization: Bearer $O1" -H "Content-Type: application/json" -d '{"price":{"amount":1}}'   # 403
```
> **401 vs 403:** 401 = not authenticated (no/invalid token); 403 = authenticated but not allowed (wrong owner). `Admin` bypasses ownership.

## 4. Demo 3 — per-service JWT validation, defense in depth (ADR-031)

The Payment service verifies the **same** token on its own HTTP endpoints — even with no gateway, the network is not a trust boundary:
```bash
curl -s -o /dev/null -w "payment GET (no token): %{http_code}\n" http://localhost:5240/payments/$ORDER   # 401
curl -s -o /dev/null -w "payment GET (with token): %{http_code}\n" http://localhost:5240/payments/$ORDER -H "Authorization: Bearer $TOKEN"   # 200/404
```

## 5. Demo 4 — PII: masking + right-to-be-forgotten (ADR-032)

`GET /users/{id}` returns full PII to the owner/Admin, **masked** to anyone else:
```bash
curl -s http://localhost:5224/api/v1/users/c1b2c3d4-0001-4000-8000-000000000001 -H "Authorization: Bearer $RAHUL"   # Rahul sees p***@tadka.test, +91••••••••01
```
GDPR right-to-be-forgotten → **anonymise** (not hard-delete — order history must reconcile). **This burns Priya** (`c1b2c3d4-0001…`) for the rest of the day — reset volumes after, or forget a throwaway user.
```bash
curl -s -o /dev/null -w "forget: %{http_code}\n" -X POST http://localhost:5224/api/v1/users/c1b2c3d4-0001-4000-8000-000000000001/forget -H "Authorization: Bearer $TOKEN"   # 204
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \"Name\",\"Email\" FROM identity.users WHERE \"Id\"='c1b2c3d4-0001-4000-8000-000000000001';"   # [deleted], deleted+…@tadka.invalid
```
> **Honest limit:** events already emitted to Kafka / the outbox aren't retro-scrubbed — we **minimise PII in events** (they carry IDs + amounts, not phone/address) and rely on crypto-shredding for the rest.

## 6. Field-level encryption at rest + payment tokenization — **weekday lab** (later numbered ADR-052 / ADR-053)

> Not in the 120-minute class. ADR-030/031/032 are the day's core. On this branch the encrypt/tokenize tests still count toward **44/44**. Later branches reuse **045** for restaurant-reject; encrypt/tokenize become **052/053**. Do not teach "ADR-045" as encryption after Day 11.

Phone is encrypted at rest with AES-GCM (`Demo:EncryptPiiAtRest`, default `true`):
```bash
docker exec tadka-postgres psql -U tadka -d tadka -c "SELECT \"Name\", \"Phone\" FROM identity.users;"   # ciphertext blobs, not plaintext
curl -s http://localhost:5224/api/v1/users/c1b2c3d4-0001-4000-8000-000000000001 -H "Authorization: Bearer $TOKEN" | grep -o '"phone":"[^"]*"'   # +919876500001 — decrypts correctly for the owner
```
Break: `docker compose down -v && docker compose up -d` (fresh volume), then `Demo__EncryptPiiAtRest=false dotnet run --project src/Tadka.Api` — the same query now shows plaintext.
> **A gotcha worth knowing:** flipping the flag back without resetting the volume throws `FormatException` on startup — AuthSeeder tries to read the OLD state's values under the NEW converter. Always reset volumes when switching this lever, same discipline as any other Demo config toggle in this cohort.

Card numbers are tokenized the instant they arrive, never logged or persisted (`Demo:LogRawCardNumber`, default `false`):
```bash
curl -s -X POST http://localhost:5240/payments/charge -H "Content-Type: application/json" -H "Authorization: Bearer $TOKEN" -d '{"orderId":"11111111-1111-4111-8111-111111111111","amount":299.00,"currency":"INR","cardNumber":"4111 1111 1111 1111"}'
docker exec tadka-payment-db psql -U tadka -d tadka_payment -c "SELECT \"OrderId\",\"CardToken\",\"CardLast4\" FROM payment.payments;"   # TOK-9BBE..., 1111 — no PAN column exists
```
Break (the anti-pattern, made visible): `Payment__LogRawCardNumber=true dotnet run --project src/Tadka.Payment.Api`, charge again — the raw card number appears in the Payment service's own console log.

## 7. Run the tests
```bash
dotnet test    # 44/44 — monolith 33 (incl. 4 auth/ownership + 4 FieldCipher) + Payment 11 (incl. per-service 401 + 6 CardTokenizer).
               # Existing suites pass via a TestAuthHandler (default Admin); X-Test-NoAuth/X-Test-Auth drive 401/403.
```

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

➡️ Next (Day 11): extract the **Delivery** service (with real-time location tracking / Redis-geo) and front the services with the **API gateway** (YARP).
