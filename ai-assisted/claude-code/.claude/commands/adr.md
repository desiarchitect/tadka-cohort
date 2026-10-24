---
description: Draft an Architecture Decision Record (ADR) with the cohort template, before any code is written
argument-hint: [decision topic]
allowed-tools: Read Glob Grep
---

Draft an ADR for this decision: $ARGUMENTS

Work in this order:

1. List `docs/adrs/` and find the highest number. The next ADR is that number plus one (the repo currently ends at 067, and there is no 046).
2. Read `docs/templates/adr-template.md` and follow it exactly.
3. Read the ADRs that touch the same area (use Grep) and quote the numbers you build on or change.
4. The ADR must answer all of these. If you cannot, say what is missing instead of inventing it:
   - **Context**: what force makes us decide now (scale, failure, team, cost)?
   - **Options**: at least three real alternatives, each with a rupee or effort estimate.
   - **Choice and why**.
   - **Trade-off**: what we give up.
   - **Failure mode**: how it breaks at 2 am, and how big the blast radius is.
   - **Revisit when**: a measurable trigger, not "later".
   - **Cross-stack equivalents**: Java and Spring, Node, Go.

Show the draft in the chat. Do **not** create the file and do **not** write code until I approve the design.
