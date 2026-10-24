---
name: Testing standards
description: How tests are written, named and fixtured in Tadka. Use when adding or changing a test.
applyTo: "tests/**/*.cs"
---

# Testing rules

1. **Libraries.** xUnit, FluentAssertions, NSubstitute. Never Moq. Use NSubstitute only at an external boundary (a payment gateway, an email relay).
2. **Names are sentences.** Snake case, describing behaviour: `Cancel_from_Created_succeeds_with_reason`, `Menu_get_returns_etag_and_if_none_match_yields_304`. Do not use a rigid `Method_State_Result` pattern.
3. **Use the existing fixtures.** Integration tests use `TadkaApiFactory`, `RestaurantApiFactory`, `PaymentApiFactory` or `DeliveryApiFactory` (and the `*KafkaApiFactory` variants for Kafka). They start the database for you. Do not start your own container unless the neighbouring tests do.
4. **Auth in tests.** Reuse `TestAuthHandler` and its `X-Test-*` headers. For real-token behaviour use the `RealAuth*` factories.
5. **Assert the response and the state.** Check the status code, the body, and that the row (or the `outbox_messages` row) exists.
6. **Boundary tests are special.** `tests/Tadka.Api.Tests/Architecture/BoundaryTests.cs` guards ADR-008 and ADR-022. Never weaken it to make a change pass.
7. **Run what you changed**, then the whole project: `dotnet test tests/<project> --filter "FullyQualifiedName~<Class>"`.
