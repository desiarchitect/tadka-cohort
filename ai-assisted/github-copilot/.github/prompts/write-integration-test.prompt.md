---
name: write-integration-test
description: Write an integration test for the selected controller action or endpoint, using the project's existing fixtures, and run it
agent: agent
---

Write an integration test for this code: ${selection}

Rules (full detail in [the testing instructions](../instructions/testing-standards.instructions.md)):

1. Find the test project for the service, and the closest existing test class (for example `RestaurantApiTests.cs`). Copy its structure and use its `*ApiFactory`. The factory starts the database, so do not start a container yourself.
2. xUnit and FluentAssertions. NSubstitute only at an external boundary.
3. Name the test as a readable snake case sentence, like `Patch_name_fills_display_name_by_default`.
4. Cover the happy path and the documented failures: 400, 401 or 403, 404, 422, 409.
5. Assert the response and the stored state (the row, or the `outbox_messages` row if the endpoint publishes an event).
6. Run only the new test with `dotnet test tests/<project> --filter "FullyQualifiedName~<Class>"`, then the whole project. Fix failures. Show the green output.
