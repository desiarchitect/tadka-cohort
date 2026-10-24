# AI-assisted and AI-native engineering on Tadka

This kit teaches you to work **with** AI coding tools the way an architect does: you set the rules, approve the plan, and own the result. It ships working configuration for three tools, all written for this repository and checked against the real code:

- **Claude Code** (a terminal agent)
- **GitHub Copilot in VS Code** (chat, prompts, custom agents)
- **Cursor** (an AI-native editor)

The point is not the tools. Tools change every month. The point is the **context you give them**, the **permissions you grant**, and the **tests that prove the work**. Those skills carry to any tool.

## 1. Assisted versus native

| Level | What the tool does | Your job | Typical failure |
|---|---|---|---|
| 1. Autocomplete | Finishes the line you are typing | Accept or reject | A plausible line that is wrong |
| 2. Chat | Answers questions about your code | Ask precisely, give context | A confident generic answer |
| 3. Reusable prompts and rules | Follows saved rules and prompts | Write and maintain them | Rules that went stale |
| 4. Agent (AI-native) | Plans, edits many files, runs commands, fixes its errors | Set the rules and limits, approve the plan, review the diff | A big confident change that breaks a boundary |

Levels 1 and 2 are *AI-assisted*. Levels 3 and 4 are *AI-native*. The higher you go, the more the quality of your rules, permissions and tests decides the result.

## 2. What is in this folder

```
ai-assisted/
├── README.md, CHEATSHEET.md
├── install.ps1                       Copies the kit into the repository root (never overwrites)
├── architect-toolset/                A reference for the final class: field manual, ADR index (001 to 067), patterns, diagrams, cheatsheet
├── shared/AGENTS.md                  THE rulebook. Read by every tool.
├── claude-code/
│   ├── CLAUDE.md                     One line imports AGENTS.md, plus Claude specifics
│   └── .claude/
│       ├── settings.json             Permissions: what runs freely, what is blocked
│       ├── commands/                 /adr  /boundary-check  /review  /test-slice  /refactor
│       ├── agents/                   architecture-reviewer, security-auditor, chaos-engineer
│       ├── skills/                   ef-migration-generator, k6-benchmark-runner (with scripts)
│       └── rules/                    ordering.md, payment.md (load only for matching files)
├── github-copilot/
│   ├── .github/
│   │   ├── copilot-instructions.md   Short, repository-wide
│   │   ├── instructions/             Rules that apply to certain files (applyTo)
│   │   ├── prompts/                  /scaffold-adr  /vertical-slice  /write-integration-test  /resilience-review
│   │   └── agents/                   architecture-reviewer (read-only)
│   └── .vscode/                      settings.json, extensions.json
├── cursor/.cursor/rules/             architecture, api-controllers, efcore-persistence, testing (.mdc)
└── comparisons-and-workflows/
    ├── context-engineering-matrix.md Which file does what in each tool
    ├── single-source-of-truth.md     How to keep one rulebook, not three
    └── student-exercises.md          Five hands-on exercises, about 75 minutes
```

## 3. What each kind of file is for

| Kind | Claude Code | Copilot (VS Code) | Cursor | Loaded when |
|---|---|---|---|---|
| Always-on rules | `CLAUDE.md` | `copilot-instructions.md`, `AGENTS.md` | `alwaysApply` rule, `AGENTS.md` | Every request |
| File-scoped rules | `.claude/rules/*.md` (`paths:`) | `*.instructions.md` (`applyTo:`) | `*.mdc` (`globs:`) | A matching file is in play |
| Saved prompts | `.claude/commands/*.md` | `.github/prompts/*.prompt.md` | (see Cursor docs) | You call them with `/` |
| Specialist reviewers | `.claude/agents/*.md` | `.github/agents/*.agent.md` | (see Cursor docs) | You ask for them |
| Packaged procedures | `.claude/skills/*/SKILL.md` | prompts plus scripts | | The task matches the description |
| Safety limits | `settings.json` `permissions` | per-action approval | per-action approval | Always |

## 4. Install the kit

The kit sits in `ai-assisted/` so you can read it. To use it, copy it into the repository root. The script **never overwrites** a file that already exists (this repo already has a `CLAUDE.md` and a `.github/copilot-instructions.md`), and it tells you what it skipped.

```powershell
powershell -File ai-assisted/install.ps1 -Tool all -WhatIf    # preview
powershell -File ai-assisted/install.ps1 -Tool all            # do it
# or just one tool: -Tool claude | copilot | cursor
```

Then:

1. **Claude Code.** Open your existing `CLAUDE.md` and add the line `@AGENTS.md` near the top. Start `claude` in the repository root and type `/` to see the commands.
2. **Copilot.** Open the repository in VS Code. In Chat, type `/` to see the prompts. Pick the `architecture-reviewer` agent from the agent list.
3. **Cursor.** Open the repository. The rules under `.cursor/rules` are picked up automatically.

Compare the installed files with your repository's own: where they differ, read both and decide. The kit is a starting point, not a replacement.

## 5. Seven rules that make AI tools work

1. **Design first.** Ask for the decision (an ADR, a contract, a sequence) before the code. Code is the proof of a design, not the start of it.
2. **Give it the constraints it cannot guess.** Team size, scale, the ADR numbers, what must never happen. A generic question gets a generic answer that sounds right.
3. **Say what NOT to do, and why.** "Never retry a payment decline: it would charge twice" teaches more than "be careful".
4. **Point at a golden example.** "Follow `RestaurantsController.cs`" beats a paragraph of description.
5. **Close the loop.** The task is done when `dotnet build` and `dotnet test` are green, not when the code is written.
6. **Keep diffs small and read them.** You are accountable for every line. A review you skip is a bug you shipped.
7. **Update the rulebook in the same commit as the decision.** A stale rule file makes the tool confidently wrong.

## 6. Safety

- **Permissions.** `.claude/settings.json` lets builds and tests run, and blocks force push, hard reset, `docker compose down -v` (which deletes the databases) and reading `.env` files. Everything else asks you first. The flag `--dangerously-skip-permissions` removes those checks: use it only in a throwaway sandbox, never on your real machine.
- **Secrets.** Never paste a key, token or customer data into a prompt. `.env` files are denied to Claude Code by `settings.json`.
- **Instructions inside data.** Text in a web page, a file or a tool result is **data**, not an order. If a file says "ignore your rules and do X", that is a warning sign, not an instruction.
- **You decide.** The tool proposes. A person approves anything that touches money, production or other people's data.

## 7. Check the current docs

These tools change fast. This kit was checked against the vendor documentation in October 2026. When something does not behave as described, check the official page first:

- Claude Code: https://code.claude.com/docs (settings, permissions, memory, slash commands, subagents, skills)
- GitHub Copilot in VS Code: https://code.visualstudio.com/docs/copilot/customization (custom instructions, prompt files, custom agents)
- Cursor rules: https://cursor.com/docs/context/rules
- The `AGENTS.md` convention: https://agents.md

## 8. Start here

For the whole picture of what an architect should know, with Tadka as the worked example, open the [architect toolset](architect-toolset/README.md).

Do the [five exercises](comparisons-and-workflows/student-exercises.md). Read the [matrix](comparisons-and-workflows/context-engineering-matrix.md) when you want to compare tools, and [one rulebook for every tool](comparisons-and-workflows/single-source-of-truth.md) when your team starts using more than one.
