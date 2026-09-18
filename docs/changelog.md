# Day 10 changelog (since Day 9)

`git checkout day-10`. Previous branch: `day-09`.

## We learned

- **JWT** (HS256) login/register; `PasswordHasher`; seeder password `Password123!`.
- **RBAC + ownership** validated **per service** — the gateway is not a trust boundary.
- **PII:** log masking, GDPR anonymise, PCI scope stays in Payment.
- `MapInboundClaims = false` so `[Authorize(Roles=…)]` sees the `role` claim.

## Architecture

- Auth in the monolith (`Auth/*`). Payment validates the **same** JWT/key on its HTTP endpoints.
- Tests: `TestAuthHandler` (default Admin; `X-Test-NoAuth` / `X-Test-Auth`).

## Code vs Day 9

| Area | What changed |
|---|---|
| `Auth/*`, login/register | Token issue |
| `[Authorize]` on Orders / Restaurants / Tracking | Role + ownership |
| Payment HTTP | 401 without token |
| `User.OwnedRestaurantId` | Migration |
| `PiiMasker`, `UsersController` | Right to be forgotten |

ADRs **030, 031, 032**.
