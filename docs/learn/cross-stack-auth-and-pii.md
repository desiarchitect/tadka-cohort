# Learn: building this in Java or Node — what actually changes

> **Weekday reading for Day 10.** In class we wired JWT authentication, RBAC + resource-ownership,
> and PII protection in .NET. The *pattern* is identical in Java or Node — a stateless signed
> token, a role check plus an ownership check, masked logs, an anonymise-not-delete endpoint. What
> changes is the tooling, and a few genuinely stack-specific decisions you'd have to make. Not
> code — the questions you'd have to answer while wiring this up elsewhere.

## 1. Issuing and validating JWTs

**The question, in any language:** where does the signing key live, and how does every service
that needs to *verify* a token get access to it without also being able to *mint* one? That's
the real architectural question — the library is secondary.

**Java (Spring Security):** `oauth2ResourceServer().jwt()` handles validation, but you have to
decide *how* it gets the key — either a static symmetric key (HS256, matching what Tadka does
today) or a JWK Set URI if you're fetching public keys from an identity provider (the RS256
migration path ADR-030 names). One genuine gotcha: Spring Security's filter chain order matters.
If your JWT filter isn't registered before your controller's security rules evaluate, you'll get
confusing 403s instead of the 401 you expect for a missing token — worth knowing before you spend
an hour debugging the wrong status code.

**Node:** `jsonwebtoken` for pure sign/verify is the closest match to what `TokenService.cs` does
today, hand-rolled and explicit. `passport-jwt` wraps that into Express middleware if you want the
framework wiring. `NextAuth` is a different animal entirely, built for web apps with OAuth
providers baked in — reaching for it to build a pure API backend's token issuance (Tadka's model)
is usually the wrong tool for the job, worth naming even though it comes up in every "how do I do
auth in Node" search result.

**What doesn't change:** the claim shape (`sub`, `role`, `restaurantId`) is a JWT-spec-level
decision, not a library one — any stack's verify step reads the same three claims off the same
signed payload. The failure mode is identical everywhere too: a JWT payload is signed, not
encrypted, so putting PII or a secret in the claims is a mistake in any language.

## 2. RBAC + resource ownership

**The question, in any language:** a role check alone answers "can this kind of user do this kind
of thing" — it can't answer "does this *specific* user own this *specific* resource." You need
both, and the interesting decision is *where* the ownership check lives in your codebase.

**Java (Spring Security):** `@PreAuthorize("hasRole('RestaurantOwner')")` handles the RBAC half
declaratively via SpEL expressions. For ownership, Spring gives you a real framework option — a
custom `PermissionEvaluator` bean, wired into `@PreAuthorize("hasPermission(#id, 'Restaurant',
'edit')")`. That's more machinery than Tadka's actual approach (a plain inline `OwnsOrAdmin(...)`
check in the controller — see ADR-031, corrected during this cohort's own build to describe what's
genuinely shipped, not the framework-idiomatic version originally drafted). The lesson transfers:
for four roles and one ownership rule, the inline check is arguably *more* honest code than the
framework indirection, and that's a real architectural opinion worth defending in an interview,
not just a .NET shortcut.

**Node:** no built-in RBAC exists. **CASL** is the most common declarative-ability library — you
define abilities like "a user `can('read', 'Order')` where `order.customerId === user.id`", which
expresses RBAC and ownership as a single rule rather than two separate checks. That's a genuinely
different shape than Tadka's split role-check-then-ownership-check, worth noticing rather than
assuming it maps 1:1. Or hand-roll middleware, matching Tadka's inline style directly.

**What doesn't change:** the 401-vs-403 distinction is HTTP semantics, not framework behavior —
"who are you" failures are always 401, "you're known but not allowed" failures are always 403,
in every stack. And the classic failure mode (validate only at a gateway, forward a plain
`X-User-Id` header, get it forged by anything that reaches your internal network) is a network
architecture mistake, not a language-specific bug — Day 10's per-service-validation fix
(ADR-031) applies identically whether the service behind the gateway is written in .NET, Java, or
Node.

## 3. PII — masking, encryption, right-to-be-forgotten

**The question, in any language:** where does PII get intercepted before it leaks — in logs, in
Kafka events, at rest in the database — and what does "delete on request" actually mean once
some of that data has already left your database as an immutable event?

**Java:** Logback ships a `TurboFilter`/custom `PatternLayout` hook for masking PII in log lines
before they're written — genuinely first-class support, not a bolt-on. For column-level
encryption, Hibernate's `@ColumnTransformer` or a JPA `AttributeConverter` lets you encrypt and
decrypt transparently on read/write, the same shape as the EF Core value converter behind
`identity.users.Phone`'s AES-GCM encryption on this branch (`Demo:EncryptPiiAtRest`, ADR-045).

**Node:** `pino`'s built-in `redact` option takes a list of paths (`'req.headers.authorization'`,
`'user.phone'`) and masks them automatically at log time — arguably a cleaner out-of-the-box
story than most .NET logging setups need to hand-roll. For column encryption, Prisma middleware
or a repository-layer wrapper does the encrypt/decrypt, since neither Prisma nor a raw `pg` client
gives you a transparent-conversion feature the way Hibernate does.

**What doesn't change, and this is the important one:** crypto-shredding (the fallback for PII
that's already on an immutable Kafka topic, see ADR-032's honest limit) requires a
per-user-or-per-field encryption key *in any stack* — the architectural decision is identical
everywhere: where do you store those keys, and how do you guarantee that deleting a key actually
makes the associated data permanently unreadable (never cache the decrypted value anywhere that
outlives the key, never log it in plaintext before it's encrypted). That discipline has nothing
to do with .NET, Java, or Node — it's a key-management design, and getting it wrong is equally
possible in every language.

---

**One thing to notice across all three sections:** the *reasoning* — where the key lives, where
the ownership check goes, where the PII gets intercepted — is identical no matter what you build
this in. The libraries differ in how much of the mechanism they hand you for free versus how much
you hand-roll. That's the actual skill this day is teaching: name the architectural question
first, and the library choice becomes a much smaller decision after.
