# Hands-on lab: working with AI tools on Tadka

**Time:** about 75 minutes. **You need:** the repo on branch `day-16` building (`dotnet build Tadka.slnx`), Docker running, and at least one of Claude Code, VS Code with Copilot, or Cursor. Install the kit first (see the README): `powershell -File ai-assisted/install.ps1 -Tool all`.

**Rules of the lab**
- Use a scratch branch: `git switch -c lab/ai-exercises`. Throw it away when you finish.
- You own every line the tool writes. Read the diff before you accept anything.
- Write down what you saw for each exercise. The notes are the deliverable.

---

## Exercise 1: Design first (15 min)

**Goal:** make the tool think in decisions before code.

**Scenario:** Tadka wants a customer loyalty scheme: 1 point per Rs 100 spent, points redeemable for discounts. Where do the points live: a column on `ordering.orders`, a column on `identity.users`, or a new service with its own database?

1. Claude Code: `/adr Customer loyalty points ledger`. Copilot: run `/scaffold-adr` and enter the same topic. Cursor: ask it to draft an ADR using `docs/templates/adr-template.md`.
2. Check the draft. Does it have: three real options with a cost each, a trade-off, a **failure mode**, a **revisit when** with a number, cross-stack equivalents?
3. Did it pick the right next number? (The folder ends at 067 and has no 046, so the answer is 068.)
4. Challenge it: "Argue against your own choice." Note whether the second answer is better.

**You learn:** the quality of the answer depends on the structure you ask for. A template is a prompt you wrote once.

---

## Exercise 2: Context experiment (15 min)

**Goal:** see for yourself that the rule files change the code you get.

1. Ask the tool, with the rule files **removed** from view (rename `AGENTS.md`, `CLAUDE.md`, `.github/copilot-instructions.md` and `.cursor/rules` to `*.off`, restart the session):
   > Add an endpoint to cancel an order.
2. Save the answer. Now restore the files, start a **new session**, and ask the identical question.
3. Compare. Look for: the route style (`api/v1/orders/...`), the status codes (422 for an illegal transition, 409 for a lost race), `Problem(...)` errors, ownership check, the allowed states (`Cancelled` only from `Created` or `Confirmed`), and whether it touched Payment directly.
4. Write down three differences.

**You learn:** the context file is most of the work. Without it, the tool gives a generic answer that sounds right.

---

## Exercise 3: A feature in the right service, with a boundary check (20 min)

**Goal:** add a small feature and prove the architecture still holds.

**Scenario:** Restaurants want to publish opening hours. Add `GET /api/v1/restaurants/{id}/operating-hours`.

1. The data belongs to the **Restaurant service** (`src/Tadka.Restaurant.Api`), not the monolith. Check that the tool picks that project. If it does not, correct it and note that.
2. Ask the tool to show the **contract first** (route, response, errors) and wait for your OK. Then let it build the entity, the Fluent configuration, the controller action and a migration **for the Restaurant service only**. Claude Code: the `ef-migration-generator` skill does the migration. Copilot or Cursor: use `/vertical-slice` or the agent.
3. Prove it:
   ```powershell
   dotnet build Tadka.slnx
   dotnet test tests/Tadka.Api.Tests --filter "FullyQualifiedName~BoundaryTests"
   dotnet test tests/Tadka.Restaurant.Api.Tests
   ```
   Claude Code: `/boundary-check` runs the first test and adds a manual check.
4. Questions to answer: Did Orders need to change? (It should not. Orders only needs hours if it prices or validates by them. If it did, which ADR says how to get the data?) Did the tool add any foreign key to another schema?

**You learn:** the tool can write the feature. Boundaries stay safe only because a test, not the tool, checks them.

---

## Exercise 4: Resilience audit with a specialist (15 min)

**Goal:** use a read-only specialist to find missing protections.

**Scenario:** a colleague wrote this call to an SMS provider.

```csharp
public async Task<bool> SendSmsAsync(string phone, string message)
{
    var response = await _httpClient.PostAsJsonAsync("/sms/send", new { phone, message });
    return response.IsSuccessStatusCode;
}
```

1. Claude Code: ask `Use the chaos-engineer subagent to audit this method` and paste the code. Copilot: select the code and run `/resilience-review`. Cursor: ask it to apply the resilience rules in `AGENTS.md`.
2. Does it find: no timeout (a default `HttpClient` waits 100 seconds), no retry with jitter, no circuit breaker, no distinction between an outage (retry) and a bad number (never retry), no fallback?
3. Compare with the real thing: open `src/Tadka.Payment.Api/Resilience/PaymentResiliencePipeline.cs`. What does Tadka do that the tool's suggestion does not?
4. Optional: break Payment on purpose with `scripts/inject-incident.ps1 -Scenario payment-outage` (and `-Scenario clear` afterwards), and watch the breaker on the Grafana dashboard.

**You learn:** a specialist with a narrow checklist finds more than a general "review this".

---

## Exercise 5: The closed loop (10 min)

**Goal:** let the tool prove its own work.

1. Open `tests/Tadka.Restaurant.Api.Tests/RestaurantApiTests.cs` and `src/Tadka.Restaurant.Api/Controllers/RestaurantsController.cs`. Pick an action that has no test yet (compare the two).
2. Claude Code: `/test-slice <that action>`. Copilot: `/write-integration-test` with the action selected. Cursor: ask for a test that follows the rules in `testing.mdc`.
3. Watch what happens when the first version fails. A good run reads the error, fixes it and runs again, without you pasting anything.
4. Finish with `/review` (Claude Code) or ask the `architecture-reviewer` agent (Copilot) to review the diff.

**You learn:** "done" means the tests are green, not that the code is written.

---

## Debrief questions

1. Which exercise would have gone wrong without a rule file, and how?
2. Where did you stop the tool, and what did you see that made you stop?
3. Name one thing you would never allow a tool to do on this repo without asking. (Look at `.claude/settings.json` for the answer the kit chose.)
4. What would you add to `AGENTS.md` after today?
