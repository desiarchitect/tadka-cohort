---
description: Refactor code with a green test run before and after, so behaviour provably does not change
argument-hint: [file, class or folder]
allowed-tools: Read Grep Glob Edit Bash(dotnet build *) Bash(dotnet test *)
---

Refactor: $ARGUMENTS

1. **Baseline.** Run `dotnet test` for the project that owns the code. If anything is red, stop and tell me. Do not touch code on a red baseline.
2. **Understand the contract.** Check whether the change could alter a public route, a response shape, a Kafka message or a database column. If it could, stop and ask first.
3. **Change in small steps.** Keep the diff small. Keep every `// (ADR-NNN)` comment. Keep nullable annotations and file-scoped namespaces.
4. **Verify.** Run `dotnet build Tadka.slnx` and the same tests again. If something broke, fix it and run again.
5. **Report.** In a few lines: what changed, why it is better (fewer branches, clearer name, one less dependency), and the passing test output. Do not claim improvements you cannot point to.
