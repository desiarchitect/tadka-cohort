# One rulebook for every tool

## The problem: drift

A team where one person uses Claude Code, another Copilot and a third Cursor ends up with three rule files. Within a month they disagree: one allows minimal APIs, another demands controllers; one knows the ADR list, another stops at ADR-044. The tool then confidently writes code for a system that no longer exists.

## The pattern this repo uses

**One canonical file, `AGENTS.md`, read by every tool.**

| Tool | How it gets `AGENTS.md` |
|---|---|
| GitHub Copilot (VS Code) | Reads the root `AGENTS.md` by itself (setting `chat.useAgentsMdFile`, on by default). |
| Cursor | Reads the root `AGENTS.md` by itself. |
| Claude Code | `CLAUDE.md` contains the single line `@AGENTS.md`. The import pulls the file in. (If a repo has an `AGENTS.md` and no `CLAUDE.md`, recent versions read `AGENTS.md` directly. Having a `CLAUDE.md` for Claude-only extras is the clearer setup.) |

Then each tool keeps **only what is specific to it**:

- Claude Code: `.claude/` (permissions, commands, subagents, skills, path-scoped rules).
- Copilot: `.github/instructions/`, `.github/prompts/`, `.github/agents/`.
- Cursor: `.cursor/rules/`.

Rule of thumb: if a rule would be true for any tool, it belongs in `AGENTS.md`. If it depends on how a tool loads files, it belongs in that tool's folder.

## Keeping the rulebook true

1. **Same commit as the decision.** When an ADR is accepted, update `AGENTS.md` in that commit. Put it in your pull request checklist.
2. **Facts you can test.** Prefer rules that point at a source of truth ("the order states are in `OrderStateMachine.cs`") over a copy of the facts. A copy is what goes stale.
3. **Short.** Past a few hundred lines, models follow every rule a little less. Move area-specific rules into scoped files (`paths:`, `applyTo:`, `globs:`).
4. **Review it like code.** Rule files are in git, so changes go through a pull request.
5. **Test the rulebook.** Once a quarter, ask each tool the same question ("add an endpoint to cancel an order") and compare answers with and without the rule files. Differences show you where the rules drifted. Exercise 2 in `student-exercises.md` is exactly this.

## Do not do this

- Do not copy the same text into three files by hand.
- Do not generate `CLAUDE.md` from another file with a script that overwrites hand-written content. If you must generate, generate into a clearly named file and import it.
- Do not use a symlink as your only plan: on Windows it needs Developer Mode or admin rights, and a relative target is easy to get wrong (a link in `.github/` pointing to the repo root is `../CLAUDE.md`, not `../../CLAUDE.md`).
