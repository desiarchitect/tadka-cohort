# Claude Code instructions: Tadka

@AGENTS.md

The line above imports the shared rules, so Claude Code, Copilot and Cursor all follow the same document. Only Claude Code specifics live here.

## Claude Code specifics

- **Plan before you edit.** For anything bigger than one file, make a plan first and wait for approval (press Shift+Tab to switch to plan mode).
- **Slash commands** in `.claude/commands/`: `/adr`, `/boundary-check`, `/review`, `/test-slice`, `/refactor`.
- **Subagents** in `.claude/agents/`: `architecture-reviewer`, `security-auditor`, `chaos-engineer`. Ask for one by name: "Use the chaos-engineer subagent to audit `PaymentResiliencePipeline.cs`".
- **Skills** in `.claude/skills/`: `ef-migration-generator` and `k6-benchmark-runner`. Claude picks one when your request matches its description.
- **Rules** in `.claude/rules/`: `ordering.md` and `payment.md` load by themselves when you touch those files.
- **Permissions** live in `.claude/settings.json`. Builds and tests run without asking. Force push, hard reset, `docker compose down -v`, `.env` files and build output are blocked.

## When you are not sure

Ask one short question instead of guessing. If the work needs a new ADR, draft it with `/adr` and wait for approval before you write code.
