---
description: Senior-level review of the current git changes against the Tadka ADRs, resilience and security rules
allowed-tools: Bash(git diff *) Bash(git status *) Read Grep Glob
---

Review the changes below as a senior engineer on this codebase.

## Changed files

!`git status --short`

## Diff (staged and unstaged, against HEAD)

!`git diff HEAD`

## What to check

1. **Architecture.** Does the change break an ADR (001 to 067)? Any cross-schema foreign key, any Orders to Payment reference, any lost `// (ADR-NNN)` comment?
2. **Resilience.** Is every outbound call (HTTP, Kafka, Redis) bounded by a timeout? Are business declines kept apart from outages, so a decline never trips the breaker?
3. **Data integrity.** Is an event saved in the same transaction as the state change (outbox)? Is a write idempotent? Is the `xmin` conflict (409) still handled?
4. **Security.** Is every endpoint `[Authorize]` or explicitly anonymous? Is ownership checked? Is PII kept out of logs? Is `MapInboundClaims = false` kept where roles are used?
5. **Observability.** Is `traceparent` carried across Kafka? Are metric labels low-cardinality?
6. **Tests.** Does a test cover the new behaviour? Do the test names read as sentences?

## Output

- **Verdict**: APPROVE or REQUEST_CHANGES.
- **Blockers**: bugs, boundary violations, missing idempotency, each with file and line.
- **Suggestions**: smaller improvements, clearly separated from blockers.
- **Proof**: the exact `dotnet test` command that would show the change is safe.

If the diff is empty, say there is nothing to review.
