# ADR-047: Login Rate Limiting + Account Lockout

**Date:** 2026-09-21
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

A prior audit flagged that `POST /api/v1/auth/login` has **no protection against a guessing
attack** — a script can hammer the endpoint at whatever rate the network allows, either spraying
one password across many emails (credential stuffing, using breached password lists) or brute-
forcing one account with unlimited attempts. Today, the only cost to an attacker is the compute to
send requests; the only signal in our logs is a spike of 401s that nobody's watching. This closes
that gap with two independent controls: a **request-rate** limit (how fast can ANYONE hit this
endpoint) and an **account** lockout (how many WRONG passwords can ONE account absorb).

## Decision

**Both controls, layered — not either/or.**

1. **IP-keyed fixed-window rate limiting** via ASP.NET Core's built-in
   `Microsoft.AspNetCore.RateLimiting` middleware (in the shared framework since .NET 7 — no new
   package). Policy `auth-write`: **5 requests / 10-second window**, `QueueLimit = 0` (reject
   immediately past the limit, not queue-and-delay — a live curl loop should visibly 429, not hang).
   Applied to `POST /login`, `POST /register`, and `POST /refresh` (register is a cheaper
   credential-stuffing target than login if left open; refresh-token guessing is exactly the kind
   of endpoint this control exists for). Trips → **HTTP 429**, JSON body `{"error": "Too many
   requests. Try again shortly."}`.
   - **Exact numbers, and why:** 5/10s is deliberately small — demoable in a few seconds of a live
     curl loop in class, not a production-tuned figure. A real deployment would set this from
     traffic data (a legitimate user retrying a mistyped password 3 times in a row must not
     get 429'd); this repo's own `appsettings.json` documents the values so they're one config
     change away from being realistic (`Auth:RateLimit:PermitLimit`/`WindowSeconds`).
   - **Keyed by remote IP** (`HttpContext.Connection.RemoteIpAddress`) — the simplest partition key
     available without extra infrastructure. Named honestly in Failure mode below: this is weak
     against a real distributed attack.
2. **Per-account failed-login lockout**, independent of the network layer entirely: `User` gains
   `FailedLoginAttempts` (int) and `LockedUntil` (nullable timestamp). Every wrong password
   increments the counter; **5 consecutive failures** sets `LockedUntil = now + 60s` and resets the
   counter (so the next window after the lock clears starts fresh, not still-at-4). A **successful**
   login resets both fields to their zero state. While locked, login is rejected regardless of
   whether the password given is actually correct.
   - **The locked-account response is IDENTICAL to "wrong password"** — a generic `401
     {"error": "Invalid credentials."}` either way, including for an email that doesn't exist at
     all. This is a deliberate trade-off (see Teaching fields): the alternative (a distinct "account
     locked, try again in Ns" response) is friendlier for a legitimate user who mistyped their
     password 5 times, but it hands an attacker two things for free — confirmation the email
     exists, and confirmation their guess-flood is landing (worth automating around). We chose not
     to leak either.

## Consequences

### Positive
- Closes the literal gap the audit found: `curl`-in-a-loop against `/login` now gets throttled
  within seconds, and a single targeted account can no longer be brute-forced at all past 5 guesses
  a minute.
- Two independent layers mean an attacker who works around one (e.g. rotates source IPs to dodge
  the rate limiter) still hits the other (the account-level counter doesn't care what IP the
  request came from).
- Config-driven thresholds (`appsettings.json`) — no code change to retune for a real deployment.

### Negative
- A legitimate user who fat-fingers their password 5 times in a row (it happens) is locked out for
  60 seconds even on attempt 6 with the RIGHT password — mild self-inflicted friction, the classic
  security/usability trade-off.
- The rate limiter's state is **in-process, in-memory** — restarting the API (a deploy) silently
  resets every counter. Fine for a single-instance teaching deployment; see Revisit-when.
- IP-based keying means every request from behind a shared NAT/corporate proxy/VPN exit shares one
  bucket — a busy office could see innocent users rate-limited by their coworkers' typos.

### Risks
- **Doesn't stop credential stuffing that respects the rate limit.** An attacker who paces requests
  at 1 every 2 seconds per IP, across many IPs (a botnet, or a residential proxy pool), stays under
  BOTH controls indefinitely — this is a real, known limitation of IP + per-account controls alone,
  named honestly rather than oversold as "brute-force protection, solved."

## Alternatives Considered

### Option A: No protection (status quo)
- Pros: zero code, zero complexity.
- Cons: exactly the audit finding — unlimited guesses at any rate.
- Why rejected: this is the gap being closed.

### Option B: Rate limiting only (no account lockout)
- Pros: simpler — one control, no new `User` columns/migration.
- Cons: a low-and-slow attack against ONE account, spread across many source IPs (or waiting out
  each 10s window), never trips the network-level control at all — the account itself has no memory
  of being attacked.
- Why rejected: the two controls defend against different attacker shapes (fast-from-one-place vs.
  slow-from-everywhere); rate limiting alone leaves the second shape completely open.

### Option C: Account lockout only (no rate limiting)
- Pros: simpler at the network layer; directly protects the thing that matters (the account).
- Cons: a script can still hammer the endpoint itself at unlimited speed against a **nonexistent**
  or **not-yet-targeted** email — CPU/DB load from password-hash verification (BCrypt/PBKDF2 is
  deliberately slow) on every single request, a resource-exhaustion angle lockout alone doesn't
  touch.
- Why rejected: rate limiting is the cheap first line of defense against sheer request volume,
  independent of which account (if any) is being targeted.

## Teaching fields

- **Topic:** brute-force / credential-stuffing protection on a login endpoint.
- **Options:** no protection | rate-limit only | lockout only | both (chosen).
- **Choice:** IP-keyed fixed-window rate limiting (5/10s, `Microsoft.AspNetCore.RateLimiting`) +
  per-account failed-attempt lockout (5 failures → 60s), both returning a generic 401.
- **Why:** the two controls catch different attacker shapes; using ASP.NET Core's built-in
  middleware means zero new dependencies for the rate-limit half.
- **Trade-off:** usability friction for a legitimate user who mistypes a password repeatedly;
  in-memory rate-limiter state doesn't survive a restart or scale past one instance.
- **Failure mode** (2 AM during dinner rush): a real traffic spike (a marketing push, a payday
  order surge — NOT an attack) can trip the SAME 429s a credential-stuffing attempt would. From the
  rate limiter's point of view alone, **a legitimate spike and an attack look identical** — this is
  named honestly, not glossed over. The account-lockout counter is a little more targeted (it only
  fires on wrong passwords against one real account), but a legitimate user who forgot they changed
  their password recently could still trip it. On-call diagnosis: check whether the 401/429 spike
  is concentrated on a few accounts (targeted attack) or spread across many with otherwise-normal
  traffic shape (real spike, retune the window).
- **Revisit when:** move rate-limiter state to Redis (already in this stack, ADR-018) once running
  more than one API instance, so the counters are shared instead of per-process; add a CAPTCHA
  (hCaptcha/Turnstile) or a managed bot-detection layer (Cloudflare, AWS WAF) at real internet
  scale, where IP-based keying alone stops being a meaningful signal.
- **Cross-stack equivalents:** Java/Spring — Bucket4j or Resilience4j's `RateLimiter` for the
  request layer, Spring Security's `AuthenticationFailureBadCredentialsEvent` listener for lockout
  bookkeeping; Node — `express-rate-limit` (or `rate-limiter-flexible` for a Redis-backed store) +
  a manual failed-attempt counter in the user record; Go — `golang.org/x/time/rate` per-key
  limiters, or `ulule/limiter`, with the same manual lockout-column approach. The pattern (two
  independent counters, one per-request/per-IP and one per-identity) is idiomatic everywhere.

## References
- ADR-030 (JWT authentication — the endpoint this protects), ADR-031 (RBAC/ownership — a separate,
  already-authenticated concern).
- `src/Tadka.Api/Auth/AuthController.cs` (login/register/refresh), `Program.cs` (`AddRateLimiter`
  policy registration), `Domain/Users/User.cs` (`FailedLoginAttempts`/`LockedUntil`),
  `Migrations/20260921133153_AddLoginLockoutAndRefreshTokens.cs`.
- `tests/Tadka.Api.Tests/Integration/RateLimitingTests.cs` — live 429 + lockout-blocks-correct-password
  + lockout-self-clears-after-cooldown.
