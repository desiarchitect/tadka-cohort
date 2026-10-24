---
name: scaffold-adr
description: Draft an Architecture Decision Record with the cohort template, before any code
agent: ask
---

Draft an Architecture Decision Record for: ${input:topic:the decision, for example "loyalty points ledger"}

Follow the template in [the ADR template](../../docs/templates/adr-template.md).

Steps:

1. Look in `docs/adrs/` for the highest number. The next ADR is one higher (the folder currently ends at 067, and there is no 046).
2. Search the existing ADRs for related decisions and quote their numbers.
3. Answer every one of these. If you cannot, say what is missing instead of guessing:
   - **Context**: what forces us to decide now (scale, failure, team, cost)?
   - **Options**: at least three real alternatives with a rupee or effort estimate each.
   - **Choice and why**.
   - **Trade-off**: what we give up.
   - **Failure mode**: how it breaks at 2 am, and the blast radius.
   - **Revisit when**: a measurable trigger.
   - **Cross-stack equivalents**: Java and Spring, Node, Go.

Show the draft in the chat. Do not write files or code until the design is approved.
