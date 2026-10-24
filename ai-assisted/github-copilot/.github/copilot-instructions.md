# Copilot instructions: Tadka

Repository-wide instructions. Copilot adds this file to every chat and agent request in this repository. Keep it short: long files make models follow every rule a little less. The detailed rules for each kind of file are in `.github/instructions/`, and the complete shared rulebook is `AGENTS.md`.

## The system

Four services plus a YARP gateway: Orders and Identity (`src/Tadka.Api`, port 5224), Payment (5240), Delivery (5250), Restaurant (5260), gateway on 8080. Each service has its own Postgres. Kafka carries events between them. The solution file is `Tadka.slnx`.

## Always

- Schema per domain. No foreign key or navigation property across schemas (ADR-003, ADR-008). Cross-domain links are plain `Guid` ids.
- Orders never references Payment (ADR-022, ADR-024). They talk through Kafka and the transactional outbox (ADR-027, ADR-028).
- Prices come from the local read model `ordering.menu_replica`, never from a call to the Restaurant service (ADR-037).
- Errors are RFC 7807 Problem Details. In controllers use `Problem(...)`. 400 malformed, 404 missing, 422 business rule, 409 lost race.
- A payment decline is final: never retried and never counted by the circuit breaker (ADR-043).
- Keep every `// (ADR-NNN)` comment. This is a teaching repository.
- Tests: xUnit, FluentAssertions, NSubstitute (never Moq), Testcontainers through the existing `*ApiFactory` classes. Name tests as readable snake case sentences.

## Never

- Never invent an ADR number. They run 001 to 067 (no 046). The next one is 068.
- Never add a NuGet package without saying why.
- Never run `docker compose down -v`. It deletes the data.

## Before you finish

Run `dotnet build Tadka.slnx` and the tests of the project you changed, and show the result.
