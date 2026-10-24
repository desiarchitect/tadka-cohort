# AI tools cheatsheet for Tadka

Keys and menus can change between versions. Type `/help` (Claude Code) or `/` (Copilot) to see the current list.

## Claude Code

| Do this | How |
|---|---|
| Start | `claude` in the repository root |
| One answer, no session | `claude -p "what does OutboxRelay do?"` |
| Make a plan before any edit | Press **Shift+Tab** to switch modes until it says plan mode |
| Check which settings loaded | `/status` |
| Reset or shrink the conversation | `/clear`, `/compact` |
| Draft a decision | `/adr <topic>` |
| Check boundaries | `/boundary-check` |
| Review my changes | `/review` |
| Write a test for an endpoint | `/test-slice <endpoint>` |
| Refactor safely | `/refactor <target>` |
| Ask a specialist | "Use the chaos-engineer subagent to audit `PaymentResiliencePipeline.cs`", or type `@` and pick it |
| Add a migration to the right service | Ask for it: the `ef-migration-generator` skill is picked by its description |

A command that runs a shell command inside the prompt looks like this (needs `allowed-tools` in its frontmatter):

```
!`git diff HEAD`
```

## GitHub Copilot in VS Code

| Do this | How |
|---|---|
| Draft a decision | Chat: `/scaffold-adr` |
| Add an endpoint end to end | Chat: `/vertical-slice` |
| Test the selected code | Select it, then `/write-integration-test` |
| Audit resilience | Select the code, then `/resilience-review` |
| Review as an architect | Pick the `architecture-reviewer` agent in the agent list |
| Add a file or selection to a question | Type `#` and choose from the list |
| Check an instruction file is used | Look at the references shown under the answer |

## Cursor

| Do this | How |
|---|---|
| Rules | `.cursor/rules/*.mdc` load automatically, see the matrix for when |
| Make a rule load on demand | @-mention it in the chat |
| Shared rulebook | Root `AGENTS.md` is read too |

## Prompt templates that work

**Design first**
```
We need <capability>. Before any code, list three options with a rupee or effort
estimate each, the trade-off, how each fails at 2 am, and when we should revisit.
Then recommend one. Use docs/templates/adr-template.md. Do not write code yet.
```

**Copy a neighbour**
```
Add <endpoint> to <project>. Open <closest existing controller> first and mirror its
route style, status codes, auth and error handling. Show me the contract and wait for my OK.
```

**Close the loop**
```
Make the change, then run dotnet build Tadka.slnx and dotnet test for the project you
changed. If anything fails, fix it and run again. Show me the green output. Do not say you
are done before that.
```

**Break your own answer**
```
Argue against your recommendation. What would make you wrong? What is the first thing
that fails in production?
```

## Commands you will run again and again

```powershell
dotnet build Tadka.slnx
dotnet test tests/Tadka.Api.Tests --filter "FullyQualifiedName~BoundaryTests"
dotnet test tests/Tadka.Restaurant.Api.Tests
powershell -File scripts/dev-cluster.ps1          # starts the five services
scripts/inject-incident.ps1 -Scenario payment-outage   # break Payment on purpose
scripts/inject-incident.ps1 -Scenario clear            # put it back
```

Never run `docker compose down -v`: it deletes the databases.
