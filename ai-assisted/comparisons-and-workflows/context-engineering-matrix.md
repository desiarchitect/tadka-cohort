# Context engineering matrix: Claude Code, GitHub Copilot, Cursor

Every AI coding tool answers the same questions: *What do I always know? What do I learn only when it matters? What can I be asked to do again and again? Who can I hand a sub-task to? What must I never touch?* The names of the files change, the questions do not.

> The rows below were checked against each vendor's own documentation in October 2026. These tools change every few weeks, so before you teach a detail, check the official page (links in the README).

## 1. Where each idea lives

| Question | Claude Code | GitHub Copilot (VS Code) | Cursor |
|---|---|---|---|
| Rules that apply to every request | `CLAUDE.md` (and `@AGENTS.md` import) | `.github/copilot-instructions.md`, or `AGENTS.md` | `.cursor/rules/*.mdc` with `alwaysApply: true`, or `AGENTS.md` |
| Rules for certain files only | `.claude/rules/*.md` with `paths:` | `.github/instructions/*.instructions.md` with `applyTo:` | `.cursor/rules/*.mdc` with `globs:` |
| Rules the model chooses to load | A skill's `description` | An `.instructions.md` with a `description` | An `.mdc` with a `description` and no globs |
| A saved prompt you run on demand | `.claude/commands/*.md`, run as `/name` (skills are the newer form) | `.github/prompts/*.prompt.md`, run as `/name` | Rules you @-mention; check Cursor's docs for its command files |
| A specialist with its own tools and prompt | `.claude/agents/*.md` (subagent) | `.github/agents/*.agent.md` (custom agent) | Check Cursor's docs |
| A packaged procedure with scripts | `.claude/skills/<name>/SKILL.md` | Check the VS Code docs (newer). Prompt files and custom agents cover most of it | Check Cursor's docs |
| Run a shell command and feed the output in | `` !`git diff` `` in a command or skill (needs `allowed-tools`) | Ask the agent to run it in the terminal | Ask the agent to run it in the terminal |
| Keep files away from the model | `permissions.deny` with `Read(...)` rules in `.claude/settings.json` (a `.claudeignore` file does nothing) | Content exclusion is a GitHub organisation setting | Cursor has its own ignore files; check its docs |
| What it may run without asking | `permissions.allow` rules | Per-action approval in the agent | Per-action approval in the agent |
| Reads `AGENTS.md` | Yes (directly, or through `@AGENTS.md`) | Yes (`chat.useAgentsMdFile`, on by default) | Yes |

Tools not covered here (Windsurf, Codex, Gemini CLI and others) mostly read `AGENTS.md` as well. That is the reason this kit keeps the real rules in `AGENTS.md`.

## 2. AI-assisted versus AI-native

| | AI-assisted | AI-native |
|---|---|---|
| The idea | You write code. The tool suggests and answers. | You describe the outcome. The tool plans, edits files, runs commands and checks its own work. |
| Examples | Inline completion, chat panel | Claude Code, Copilot agent mode, Cursor agent |
| Your job | Accept or reject each suggestion | Set the rules, approve the plan, review the diff, own the result |
| Typical failure | A plausible line that is wrong | A large, confident change that breaks a boundary |
| What protects you | Reading what you accept | **Context files, permissions, tests, small diffs** |

The more autonomy the tool has, the more the quality of your context files and tests matters. That is why this kit teaches the files, not the tool.

## 3. How context is loaded (so you know why a rule was ignored)

**Claude Code.** At the start of a session it reads `~/.claude/CLAUDE.md` and the project `CLAUDE.md` (or `AGENTS.md` when there is no `CLAUDE.md`). A `.claude/rules/*.md` file with `paths:` loads only when Claude reads or edits a matching file. A skill's description is always visible, and its full text loads only when it is used. Subagents run in their own context window and return a summary.

**GitHub Copilot.** `copilot-instructions.md` and `AGENTS.md` attach to chat requests. An `.instructions.md` file attaches when its `applyTo` pattern matches the files in play, or when its `description` fits the task. A prompt file runs only when you call it.

**Cursor.** `alwaysApply` rules are always in. A rule with `globs` attaches when a matching file is in context. A rule with only a `description` is pulled in when the agent decides it is relevant. A rule with none of these loads only when you @-mention it.

## 4. Which tool when

| Task | A good fit | Why |
|---|---|---|
| Finish the line you are typing, explore an API | Copilot or Cursor in the editor | Instant, you stay in flow |
| Change many files and prove it with tests | Claude Code, Copilot agent or Cursor agent | It can run the build and the tests and fix its own errors |
| Review a diff against your ADRs | A read-only subagent or custom agent | It cannot change anything by accident |
| Repeat the same prompt every week | A command or prompt file | Saved, shared, reviewed in git |
| Anything touching secrets, production or money | You, with the tool as a reviewer | Responsibility does not move to the tool |

## 5. Anti-patterns

1. **The novel prompt.** A 300-word prompt typed from memory, every time. Save it as a command or prompt file.
2. **The context volcano.** Build output and generated files in the model's view. Deny `bin/`, `obj/` and result folders.
3. **Passive trust.** Accepting a change without running the tests. The tool is done when the tests pass, not when the code is written.
4. **The stale rulebook.** A rule file that describes last month's system makes the tool confidently wrong. Update it in the same commit as the decision.
5. **Rules without a reason.** "Never use X" with no why. The model, and the next developer, cannot judge the exceptions.
