# AI context files — what each one is for

How Claude Code and GitHub Copilot learn *this* repo, and every file you can add later under `.github/` and `.claude/`.

Install the tools in [`SETUP.md`](../../SETUP.md). The Day 1 demo is Copilot Agent Mode adding `/health/ready` — it only matches our conventions if it **read** the instructions file first.

> Paths and setting names move between tool releases. The **shape** below is the part to remember; check current Copilot / Claude docs before depending on an exact filename.

---

## The altitude rule

AI is excellent at writing a method to a signature. It is dangerous at “should this be a microservice?” because that answer depends on **your** team size, load, and payment SLA — facts that are not in the training set.

**AI writes code. You make the decision.** Context files exist so the code it writes matches *this* architecture, not a generic blog post.

---

## Today in Tadka (open these)

We ship **two** files. That is 80% of the value. Do not create the rest of the tree until you have a job for it.

| File | Tool | Role |
|------|------|------|
| [`CLAUDE.md`](../../CLAUDE.md) | Claude Code | Project memory: loaded at the start of every session |
| [`.github/copilot-instructions.md`](../../.github/copilot-instructions.md) | GitHub Copilot | Repo-wide rules for chat / Agent Mode |

Overlap between the two is fine. **Do not merge them into one file** — each tool loads its own path.

**What belongs in them**

- What the project is, in a few sentences
- Stack and the commands to build, test, run
- Layout (where things live *today*)
- Rules **with reasoning** — not only “use PATCH” but “because updates are partial”
- Gotchas, and what **not** to generate

**What does not belong**

- A copy of the code (the tool can read the code)
- Long API listings that will go stale
- Aspirations — describe what *is*, not Week 8

Keep them short. They are loaded often; five hedging pages cost more than they teach.

---

## Instructions vs prompts vs skills vs agents

This is the distinction that makes the rest of the catalog make sense.

| | Instructions | Prompts / commands | Skills | Agents |
|---|---|---|---|---|
| **When it runs** | Always on | You invoke it | The model picks it up when the task matches | You **select** a named persona |
| **What it is** | Standing house rules | A saved prompt you run again | A packaged capability (`SKILL.md` + optional scripts) | A specialist with its own prompt and tools |
| **Copilot** | `.github/copilot-instructions.md` and `instructions/` | `.github/prompts/` | `.github/skills/` (also `.claude/skills/`) | `.github/agents/*.agent.md` |
| **Claude Code** | `CLAUDE.md` | `.claude/commands/` | `.claude/skills/` | `.claude/agents/` |

**Yes — `.github/` can hold skills and agents**, not only Copilot instructions. Agent Skills is an [open standard](https://agentskills.io) (`SKILL.md`). Copilot looks in `.github/skills/`, `.claude/skills/`, and `.agents/skills/`. Claude Code uses `.claude/skills/` and `.claude/agents/` as well.

---

## The full tree (what *can* live here)

None of the folders below exist in Tadka on Day 1 except `.github/copilot-instructions.md`. They are the map for later.

```
tadka/
├─ CLAUDE.md                          Claude Code project memory         ★★★  ← we have this
├─ AGENTS.md                          vendor-neutral agent instructions  ★★
├─ .claude/                           Claude Code (none of this yet)
│  ├─ settings.json                   team settings, committed           ★★
│  ├─ settings.local.json             your overrides, gitignored
│  ├─ commands/
│  │  └─ adr.md                       slash command  →  /adr             ★★★
│  ├─ skills/
│  │  └─ adr-writer/SKILL.md          packaged capability                ★★
│  └─ agents/
│     └─ architecture-reviewer.md     named subagent                     ★★
├─ .mcp.json                          MCP servers for this project       ★
├─ .github/
│  ├─ copilot-instructions.md         Copilot repo-wide rules            ★★★  ← we have this
│  ├─ instructions/
│  │  └─ tests.instructions.md        path-scoped (applyTo glob)         ★★
│  ├─ prompts/
│  │  └─ adr.prompt.md                reusable Copilot prompt            ★★
│  ├─ skills/
│  │  └─ adr-writer/SKILL.md          Copilot agent skill                ★★
│  ├─ agents/
│  │  └─ architecture-reviewer.agent.md  Copilot custom agent            ★★
│  └─ workflows/                      GitHub Actions CI — not AI context
├─ .editorconfig                      formatting both tools respect      ★★
└─ .vscode/
   ├─ extensions.json                 recommended extensions             ★
   └─ settings.json                   editor + Copilot settings          ★
```

★★★ do first · ★★ once the basics work · ★ nice to have

---

## Always-on instruction files

### `CLAUDE.md` ★★★

Claude Code reads this automatically. Tadka’s is the worked example: stack, commands, “no Domain/ folder yet”, gotchas.

Scope: **project** rules go in the repo. Personal habits belong in a file outside the repo, not here.

### `.github/copilot-instructions.md` ★★★

Copilot’s equivalent. The Day 1 Agent-Mode demo exists to show this file working: Controllers not Minimal APIs, `TadkaDbContext` not a new connection, no Redis/Kafka.

A **stale** copy of this file is worse than none — the model generates the old architecture *confidently*. When a decision lands, update this file in the **same commit**.

### `AGENTS.md` ★★

A vendor-neutral instructions file that several coding agents look for. If you only use Copilot, keep `.github/copilot-instructions.md`. If you only use Claude, keep `CLAUDE.md`. If the team uses both (or more), put the shared substance in `AGENTS.md` and keep the tool-specific files thin.

---

## `.github/` — Copilot (and GitHub)

### `instructions/*.instructions.md` ★★

Repo-wide rules get long. These files have `applyTo` frontmatter so a rule only fires for matching paths.

```markdown
---
applyTo: "**/*.Tests/**/*.cs"
---
Use xUnit. Name tests Method_Scenario_ExpectedBehavior.
```

**Add when** a rule is genuinely local (tests, controllers, migrations) and is cluttering `copilot-instructions.md`.

### `prompts/*.prompt.md` ★★

A prompt you run on purpose from Copilot Chat (reusable reviews: ADR skeleton, failure-mode pass). **You** invoke it. Not always-on.

### `skills/<name>/SKILL.md` ★★

A folder with a required `SKILL.md` (open standard: name + description + body; optional scripts/resources). Copilot **loads it when the task matches** the description.

Same format as Claude skills. Copilot also discovers `.claude/skills/` and `.agents/skills/`.

**Add when** you have a repeatable multi-step job (write an ADR, debug a compose failure) that is more than a paragraph of standing rules.

### `agents/*.agent.md` ★★

A **named Copilot custom agent**: persona, instructions, which tools it may use. You **select** it (agent picker / `/agent`). Different from a skill: a skill is a capability any agent can pick up; a custom agent is a specialist you switch into.

**Add when** you want a persistent “architecture reviewer” or “test writer” instead of one more prompt.

### `workflows/` — not an AI file

`.github/workflows/*.yml` is GitHub Actions (CI). It lives next to Copilot files because GitHub owns `.github/`. Do not put model instructions in a workflow YAML and do not expect Copilot to treat workflows as context files.

---

## `.claude/` — Claude Code

None of this is in the Day 1 tree.

### `settings.json` / `settings.local.json` ★★

Team permissions and hooks, **committed**. `settings.local.json` is **yours** and should be gitignored (pre-approve `git status`; never auto-approve `git push --force`).

### `commands/*.md` ★★★

A file `.claude/commands/adr.md` becomes `/adr`. The file body is the prompt; `$ARGUMENTS` is whatever you typed after the command.

Highest leverage after the two main files: a review that is one keystroke actually gets run.

### `skills/<name>/SKILL.md` ★★

Packaged capability. Claude (and Copilot) can load it when the description matches. A **command** is something you invoke; a **skill** is something the model can pick up. Start with commands.

### `agents/*.md` ★★

Named subagent with its own prompt and **tool permissions**. A reviewer that *cannot write files* is the useful first one — you get a review, not a surprise refactor.

---

## Around the two tools

| File | Use |
|------|-----|
| `.mcp.json` | MCP servers (DB, issue tracker). Another dependency. Add **one** when you have felt the specific pain, not before. Same restraint as Kafka. |
| `.editorconfig` | Formatting. Both tools respect it. Cheap; worth having. |
| `.vscode/extensions.json` | “Please install Copilot / C# Dev Kit.” Not model context. |
| `.vscode/settings.json` | Editor + Copilot UI settings. |

---

## Adopt in this order (not this afternoon)

1. **`CLAUDE.md` and `.github/copilot-instructions.md`** — today. Already in this repo. Read them.
2. **One command or prompt** — the review you skip when busy (`/adr`, an ADR prompt).
3. **Path-scoped `instructions/`** — when the main file is getting long.
4. **A skill** — when the same multi-step job keeps coming back.
5. **A reviewer agent** — when you want a named persona that cannot edit.
6. **MCP** — last, and only for a pain you can name.

---

## The failure mode

The usual failure is not “we never wrote a context file.” It is:

> We wrote it in week one, the architecture moved, nobody updated it.

Then the tool generates the old system with authority. **Definition of done:** when an ADR lands, its rule goes into `CLAUDE.md` / `copilot-instructions.md` in the **same commit**.
