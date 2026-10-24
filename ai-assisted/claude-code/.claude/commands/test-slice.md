---
description: Write an integration test for one endpoint using the project's existing test fixtures, then run it until it is green
argument-hint: [endpoint or controller, for example "GET /api/v1/restaurants/{id}"]
allowed-tools: Read Grep Glob Edit Bash(dotnet build *) Bash(dotnet test *)
---

Write an integration test for: $ARGUMENTS

## Use what already exists

1. Find the test project that matches the service: `tests/Tadka.Api.Tests`, `tests/Tadka.Restaurant.Api.Tests`, `tests/Tadka.Payment.Api.Tests` or `tests/Tadka.Delivery.Api.Tests`.
2. Open its factory (`TadkaApiFactory`, `RestaurantApiFactory`, `PaymentApiFactory` or `DeliveryApiFactory`) and the closest existing test class, for example `RestaurantApiTests.cs`. **Copy their structure.** The factory already starts the database it needs, so do not start your own container.
3. Reuse the auth helpers the neighbouring tests use (`TestAuthHandler`, the `X-Test-*` headers). Do not invent a new way to log in.

## Rules

- xUnit and FluentAssertions. NSubstitute only at an external boundary such as a payment gateway.
- Name the test as a readable sentence in snake case, like `Menu_get_returns_etag_and_if_none_match_yields_304`.
- Cover the happy path and the failure the endpoint documents: 400 for malformed input, 401 or 403 for auth, 404 for missing, 422 for a broken business rule, 409 for a lost race.
- Assert the response **and** the state: query the database to prove the row exists. If the endpoint stages an outbox event, assert the `outbox_messages` row.

## Prove it

Run only your test, then the whole project:

```
dotnet test tests/<project> --filter "FullyQualifiedName~<YourTestClass>"
dotnet test tests/<project>
```

If it fails, read the error, fix the test or the setup, and run again. Do not say you are done until both are green. Report the file path you added.
