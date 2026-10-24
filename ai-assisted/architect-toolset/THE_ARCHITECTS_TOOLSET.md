# The Architect's Toolset

**What an architect should know, write, draw, use and say.** This is the one-page-per-topic map you keep open for the rest of your career. It covers documents, diagrams, techniques, tools, the knowledge map, AI engineering, and the people side of the job.

Tadka is the worked example. Where Tadka has a real file for something, the path is given, so you can open it and see the real thing. Where Tadka does not have it, the text says so.

> **How to read this.** You do not need all of it on day one. Read section 1 and section 12 first. Then use the rest as a reference when a task needs it. Tools change every year. The documents, the diagrams and the thinking do not.

## Contents

1. [What an architect actually does](#1-what-an-architect-actually-does)
2. [The documents an architect writes](#2-the-documents-an-architect-writes)
3. [The diagrams an architect draws](#3-the-diagrams-an-architect-draws)
4. [Techniques and methods](#4-techniques-and-methods)
5. [The knowledge map: what to know](#5-the-knowledge-map-what-to-know)
6. [Tools, by category](#6-tools-by-category)
7. [AI engineering for architects](#7-ai-engineering-for-architects)
8. [Making and defending decisions](#8-making-and-defending-decisions)
9. [Communication and leadership](#9-communication-and-leadership)
10. [The architect's operating rhythm](#10-the-architects-operating-rhythm)
11. [Reading list and sources](#11-reading-list-and-sources)
12. [Checklists: first 30 days, self-assessment, quick reference](#12-checklists)

---

## 1. What an architect actually does

An architect makes the decisions that are **expensive to change later**, makes them **visible**, and makes sure the system **keeps following them**.

| Verb | What it means in practice |
|---|---|
| **Frame** | Turn a vague business wish into requirements, constraints and a measurable target ("checkout p99 under 500 ms at 25 orders a second") |
| **Decide** | Compare real options, pick one, write down why, and write down when to revisit |
| **Communicate** | Draw it, write it, explain it to engineers, managers and executives in their own words |
| **Enforce** | Turn decisions into tests, checks and guardrails so they survive team changes |
| **Learn** | Measure the real system, find where the design was wrong, and change it |

### Kinds of architect (the titles differ, the work overlaps)

| Kind | Looks after | Typical output |
|---|---|---|
| **Solution architect** | One product or project end to end | HLD, integration design, vendor choices |
| **Software or application architect** | The inside of one system | Structure, patterns, ADRs, code standards |
| **Enterprise architect** | How many systems fit the business | Capability maps, roadmaps, standards, governance |
| **Data architect** | Storage, pipelines, analytics, data quality | Data models, flow diagrams, retention rules |
| **Cloud or infrastructure architect** | Platforms, networks, environments, cost | Landing zones, IaC modules, cost models |
| **Security architect** | Trust, identity, data protection | Threat models, control designs |
| **AI or ML architect** | Models, data, serving and the AI product around them | RAG and agent designs, evaluation plans, cost models |
| **Staff or principal engineer** | The hardest cross-team technical problems | Design docs, prototypes, mentoring |

### The T-shape

Be **deep in one or two areas** (for you it may be payments, data, or reliability) and **wide enough in the rest** to ask the right question and spot a wrong answer. Section 5 is the width map.

### The day-to-day loop

```
Problem  ->  Options  ->  Trade-offs  ->  Decision (ADR)  ->  Build a small proof  ->  Measure  ->  Revisit
```

This is the "Architect's Sequence" used in every Tadka day. Code is the proof of a design, not the start of it.

**What you give up as an architect:** you write less code, and your work is judged by systems you do not fully control. The trade is leverage: one good decision helps fifty engineers.

---

## 2. The documents an architect writes

Good architecture documents are **short, dated, owned, and kept next to the code**. A 60-page document nobody opens is worse than a 2-page one everybody trusts.

### 2.1 The catalogue

| Document | Answers | Audience | When | Tadka example |
|---|---|---|---|---|
| **ADR** (Architecture Decision Record) | Why did we choose this, and when do we revisit? | Engineers now and in two years | Every significant decision | `docs/adrs/` (001 to 067), template in `docs/templates/adr-template.md` |
| **Design doc or RFC** | What exactly are we about to build, and what else did we consider? | The team, neighbouring teams, reviewers | Before building anything bigger than about a week | Tadka keeps no separate file: the ADR plus the day's runbook play this role |
| **Architecture overview** | What does the whole system look like today? | New joiners, auditors, management | Kept current at every release | `docs/architecture.md` |
| **HLD** (high-level design) | Which components exist and how do they talk? | Product, engineering, ops | Start of a project | The C4 diagrams in `docs/diagrams/` |
| **LLD** (low-level design) | Classes, tables, state machines, error cases for one component | Developers building it | Before coding a hard component | `OrderStateMachine.cs` is the code form of an LLD |
| **API contract** | What is the exact request, response and error for each call? | Client and server teams | Before the controller is written | REST under `/api/v1`, Problem Details errors (ADR-005, 006, 010) |
| **Event contract** | What does each Kafka message contain and how may it change? | Producers and consumers | When a topic is created | ADR-050 (additive-only versioning) |
| **Data model and dictionary** | Which tables, keys, owners, retention, and PII? | Developers, data team, compliance | With each schema change | Schema per domain (ADR-003), no cross-schema keys (ADR-008) |
| **NFR or quality-attribute scenarios** | How fast, how available, how secure, measured how? | Everyone who signs off | Before design | "Menu and list p99 under 300 ms" (`k6/lib.js`) |
| **SLO and error-budget doc** | What do we promise users and what happens when we miss it? | Product, SRE, management | Before launch | SLO in the Day 16 runbook |
| **Capacity plan** | How much load, how much hardware, how much money? | Engineering, finance | Before launch and each year | `docs/cost-model.md`, Day 16 stress results |
| **Threat model** | Who attacks what, how, and what stops them? | Security, developers | Before building anything that handles money or identity | ADR-030 to 032, 052, 053, 065 to 067 |
| **Runbook** | How do I run, check, fix and roll back this on a bad night? | On-call engineers | Before the first deploy | `docs/runbooks/` |
| **Incident postmortem** | What happened, why, and what changes so it cannot repeat? | Everyone, blameless | After every serious incident | (template below) |
| **Migration or rollout plan** | How do we move without downtime, and how do we undo it? | Engineering, ops | Before any extraction or schema change | ADR-038 expand and contract, `scripts/backfill-menu-replica.ps1` |
| **Test strategy** | What do we test where, and what proves the architecture holds? | Developers, QA | Per system | Boundary tests, Testcontainers, k6 |
| **Cost model** | What does this cost per month, per order, per customer? | Finance, product | Before choosing, then monthly | `docs/cost-model.md`, the Azure session notes in the Day 16 runbook |
| **Risk register** | What could go wrong, how likely, how bad, who owns it? | Management, architects | Reviewed monthly | (keep in a table, below) |
| **Technology radar** | What do we adopt, trial, assess, or hold? | All engineers | Twice a year | (not in Tadka) |
| **Vendor or technology evaluation** | Which product should we buy or adopt, and why? | Management, engineering | Before a purchase or a big adoption | The Kafka, Redis, Postgres choices in the ADRs |
| **Proof of concept (spike) report** | Did the risky idea work, with numbers? | The deciding group | When one unknown blocks a decision | `evolution/` and the load tests are measured proofs |
| **Roadmap** | In what order do we change the architecture, and why? | Management, teams | Quarterly | The 16-day evolution |
| **Glossary and domain model** | What does each word mean in this business? | Everyone | From day one | Order, Rider, Menu, Settlement |
| **Compliance papers** (PCI scope, DPIA, audit trails) | Are we allowed to run this, and how do we prove it? | Auditors, legal | Before launch in a regulated area | ADR-032, ADR-053 |
| **Onboarding guide** | How does a new engineer become useful in a week? | New joiners | Kept current | `docs/runbooks/README.md` |

### 2.2 Writing rules that make documents survive

1. **One purpose, one audience.** If you need two audiences, write two documents.
2. **Date it, name an owner, and say its status** (draft, accepted, superseded).
3. **Keep it in git, next to the code.** Documents in a wiki rot. Documents in the repo get reviewed with the change that makes them true.
4. **Facts you can test beat facts you copy.** Write "the order states are in `OrderStateMachine.cs`" instead of pasting the list.
5. **Say what you rejected and why.** The rejected options are the most useful part of a decision record.
6. **Numbers over adjectives.** "Fast" is an opinion. "p99 under 300 ms" is a requirement.
7. **A document is not done until someone who did not write it can use it.**

### 2.3 Skeletons you can copy

**ADR** (the full template is `docs/templates/adr-template.md`)

```
# ADR-NNN: <the decision, as a short phrase>
Status: Proposed | Accepted | Superseded by ADR-MMM
Context:      what force makes us decide now (scale, failure, team, cost, law)?
Options:      at least three real choices, each with a cost or effort estimate
Decision:     what we chose
Why:          the reasoning, tied to numbers where possible
Trade-off:    what we give up
Failure mode: how it breaks at 2 am, and how far the damage spreads
Revisit when: a measurable trigger ("when orders pass 10 lakh a day"), not "later"
Cross-stack:  how it looks in Java, Node, Go
```

**Design doc or RFC**

```
1. Summary (3 lines)           6. Alternatives considered
2. Goals and non-goals         7. Failure modes and how we detect them
3. Background and requirements 8. Security, privacy, cost
4. Proposed design (diagram)   9. Rollout and rollback plan
5. API and data changes       10. Open questions, and who decides
```

**NFR (quality-attribute) scenario.** Write each requirement in this shape so it can be tested:

```
Source: a customer      Stimulus: places an order during the dinner rush
Environment: normal load, 25 orders a second
Artifact: the order API
Response: the order is accepted and saved
Measure: 201 returned in under 500 ms at p99, with no order lost
```

**Blameless postmortem**

```
What happened (timeline, in UTC)      What went well
Impact (users, money, duration)       What went badly
Root causes (technical and process)   Where we got lucky
Fixes: owner + date + how we verify   Lessons for other teams
```

**Risk register row**

| Risk | Likelihood | Impact | Early warning | Mitigation | Owner | Review date |
|---|---|---|---|---|---|---|
| Payment queue falls behind at burst | High | Orders stay `Created` for minutes | Consumer lag on `order-placed` | More partitions and consumers, load test | Payments lead | Monthly |

**What you give up:** time. A good ADR takes 30 to 60 minutes. The saving is every future meeting that begins with "why did we do it this way?"

---

## 3. The diagrams an architect draws

A diagram answers **one question** for **one audience**. If a diagram tries to answer three questions, split it into three.

### 3.1 The catalogue

| Diagram | The question it answers | Level | Notation or tool |
|---|---|---|---|
| **System context** | Who uses the system, and what outside systems does it touch? | The whole system as one box | C4 level 1 |
| **Container diagram** | What are the deployable pieces (services, databases, queues) and how do they talk? | One system, zoomed in | C4 level 2 |
| **Component diagram** | What is inside one service? | One service, zoomed in | C4 level 3 |
| **Code diagram** | How are these classes related? | One module | UML class, usually generated |
| **Deployment or infrastructure** | Where does each piece run (regions, zones, networks, clusters)? | Environment | Cloud icons, C4 deployment |
| **Sequence diagram** | In what order do these parties talk for this one scenario? | One flow | UML sequence |
| **Flowchart** | How does this process decide and branch? | One process | Flowchart |
| **State machine** | What states can this thing be in, and which moves are legal? | One entity | UML state |
| **ER diagram** | Which tables and relations exist? | One database | ERD |
| **Data flow diagram** | Where does data come from, where does it rest, where does it leave? Used for security and privacy | System | DFD |
| **Domain or context map** | Which bounded contexts exist and how do they relate (upstream, downstream, shared)? | Business | DDD context map |
| **Event storming board** | What happens in the business, in time order, and who reacts? | Business | Sticky notes (Miro, FigJam, paper) |
| **Network topology** | What is public, private, peered, behind which firewall? | Infrastructure | Network diagram |
| **Dependency graph** | Who calls whom, and where is the cycle? | Code or services | Generated from code or traces |
| **Trace waterfall** | Where did this one request spend its time? | One request | Jaeger or Tempo |
| **Capacity or latency budget** | Where do the 500 ms go, hop by hop? | One path | Table or bar chart |
| **Roadmap or timeline** | In what order do we change things? | Months | Timeline |
| **Decision tree** | Which option do I pick in which situation? | Choice | Flowchart |
| **Wardley map** | Which parts are commodity, and which are our edge? | Strategy | Wardley map |

### 3.2 The C4 model in four sentences

- **Level 1 (context):** you, the system, and its neighbours. No technology names.
- **Level 2 (containers):** the things you deploy, with technology and protocol on each arrow.
- **Level 3 (components):** the parts inside one container.
- **Level 4 (code):** classes. Draw it only when someone asks, and generate it.

Tadka's real C4 views are in `docs/diagrams/` and in [DIAGRAMS.md](DIAGRAMS.md). For **one real Tadka example of every diagram type in this table**, open the [Tadka Diagram Gallery](TADKA_DIAGRAM_GALLERY.md).

### 3.3 Rules of a good diagram

1. **Title it with the question.** "How does an order get paid?" beats "Payment diagram".
2. **Eight to ten boxes.** More than that, and nobody can hold it in their head. Split it.
3. **Label every arrow** with what flows and how (`order-placed event`, `HTTP GET`). An unlabelled arrow hides the real design.
4. **Colour means something**, and a small legend says what.
5. **Draw the before and the after.** Show the wound, then the fix. A fix without its wound teaches nothing.
6. **Show failure.** Draw what happens when one box is down.
7. **Sketch it live first** on a whiteboard, then polish. People learn from watching boxes appear.
8. **Keep one source of truth.** Prefer diagrams as code (below) so they change with the system.

### 3.4 Diagram tools

| Kind | Tools (examples, pick one and learn it well) | Good for |
|---|---|---|
| **Diagrams as code** | Mermaid, PlantUML, Structurizr (C4), D2 | Diagrams that live in git and update with a pull request |
| **Whiteboard style** | Excalidraw, tldraw, Miro, FigJam | Workshops and quick sketches |
| **Polished drawing** | draw.io (diagrams.net), Lucidchart, Visio, Figma | Slides and formal documents |
| **Cloud icon sets** | AWS, Azure and Google architecture icon packs | Deployment diagrams |
| **Generated from reality** | Jaeger or Tempo service maps, cloud topology views, dependency analysers | Showing what is actually running |

**What you give up:** diagrams as code are less pretty and need a little syntax. You gain diagrams that are reviewed, versioned and never out of date by accident.

---

## 4. Techniques and methods

Techniques are **repeatable ways to think**. Pick the lightest one that answers your question.

### 4.1 Finding requirements

| Technique | Use it to | How it works in one line |
|---|---|---|
| **Quality-attribute workshop** | Find the non-functional needs | List attributes (performance, availability, security, cost, changeability...) and write scenarios for the top five |
| **Back-of-the-envelope estimates** | Check an idea is in the right size | Daily users to requests per second to storage per year (Tadka: 1 lakh orders a day is about 1.2 a second, 25 at burst) |
| **Event storming** | Understand a business flow with the people who run it | Sticky notes of business events in time order, then commands, then who reacts |
| **User story mapping** | Decide what to build first | Lay out the customer journey, slice a thin end-to-end first release |
| **Constraint listing** | Avoid late surprises | Write down law, budget, team size, deadline, existing systems before designing |

### 4.2 Designing

| Technique | Use it to | One line |
|---|---|---|
| **Domain-driven design** | Draw service boundaries around the business | Bounded contexts, aggregates, a shared language, a context map |
| **C4, 4+1 views, arc42** | Describe a system from several views | Each gives a standard set of views and sections |
| **Architecture styles comparison** | Pick the overall shape | Monolith, modular monolith, microservices, event-driven, serverless (each has a cost) |
| **Make or buy matrix** | Decide build, buy or adopt open source | Score cost, fit, lock-in, skills, risk, exit cost |
| **One-way versus two-way doors** | Decide how much care a decision needs | A reversible choice is made fast, an irreversible one is made slowly |
| **Option-space table** | Never present a single option | Three or more options, cost, risk, when to switch |
| **Cross-stack mapping** | Make a pattern independent of the language | The pattern is the lesson, the library is interchangeable |

### 4.3 Judging a design

| Technique | Use it to | One line |
|---|---|---|
| **ATAM** (Architecture Trade-off Analysis Method) | Review a design against quality goals | Scenarios, sensitivity points, trade-off points, risks |
| **Risk storming** | Find what a group fears most | Everyone marks risks on the diagram, then you vote |
| **Failure mode analysis (FMEA)** | Know how each part fails | For each component: how it fails, how we notice, how bad, how likely |
| **Threat modelling (STRIDE)** | Find security holes before attackers | For each arrow and store: Spoofing, Tampering, Repudiation, Information disclosure, Denial of service, Elevation of privilege |
| **Architecture fitness functions** | Make rules enforce themselves | An automated test per rule (Tadka: `BoundaryTests.cs` fails the build on a cross-schema key) |
| **Architecture review board (ARB)** | Get an outside challenge | Short, scheduled, with a written outcome. It should unblock, not delay |
| **Evolutionary architecture** | Change safely over years | Small steps, fitness functions, a way back |

### 4.4 Proving a design

| Technique | Use it to | Tadka example |
|---|---|---|
| **Spike or proof of concept** | Remove one big unknown cheaply | A day of prototype, thrown away or kept, with measured numbers |
| **Load testing** (smoke, average, stress, spike, soak) | Find where the system bends | `k6/` and the Day 16 runbook |
| **Chaos engineering and game days** | Check the design survives real failure | `scripts/inject-incident.ps1` (payment outage, Redis down, Kafka down) |
| **Contract testing** | Keep services compatible without end-to-end tests | Consumer-driven contracts (for example Pact) |
| **Canary and blue-green releases** | Release with a way back | ADR-061 weighted canary at the gateway |
| **Observability review** | Prove you can debug it | One trace across all services in Jaeger |

### 4.5 Changing a running system

| Technique | One line |
|---|---|
| **Strangler fig** | Put a gateway in front, move one route at a time to the new service |
| **Expand and contract** | Add the new, copy the data in small chunks, switch reads, then remove the old (ADR-038) |
| **Branch by abstraction, feature flags** | Ship the new path dark, turn it on gradually (ADR-058) |
| **Outbox and dual-write removal** | Stop writing to two places at once (ADR-028) |
| **Parallel run** | Run old and new together and compare outputs |

**What you give up:** every technique costs time. Use a heavy one (ATAM, a formal threat model) when the decision is hard to reverse or touches money, safety or law. Use a light one (a table and a call) for everything else.

---

## 5. The knowledge map: what to know

Each row is a topic, what to be able to explain, and where Tadka shows it. "Know" means you can explain the trade-off, not recite a definition.

### 5.1 Foundations

| Topic | Be able to explain | In Tadka |
|---|---|---|
| **Networking** | DNS, TCP, TLS, HTTP/1.1, 2 and 3, load balancers (L4 and L7), CDNs, what a network hop costs | Gateway, Front Door in the Azure design |
| **Operating systems and runtimes** | Processes, threads, memory, file descriptors, garbage collection, thread pools | Thread-pool and memory tiles on the Grafana dashboard |
| **Databases (relational)** | Indexes, query plans, transactions, isolation levels, locks, MVCC, replication, connection pools | ADR-012, 014, 015, 016, 056 |
| **Latency and capacity math** | Latency numbers, percentiles, Little's law, queueing, why averages lie | `SYSTEM_DESIGN_CHEATSHEET.md` |

### 5.2 Application architecture

| Topic | Be able to explain | In Tadka |
|---|---|---|
| **Styles** | Monolith, modular monolith, microservices, event-driven, serverless, and when each is wrong | Days 1 to 12 |
| **Domain-driven design** | Bounded context, aggregate, entity, value object, anti-corruption layer | Schema per domain, value objects as records |
| **Clean code boundaries** | Layers, ports and adapters, dependency direction | `BoundaryTests.cs` |
| **API design** | REST, status codes, versioning, idempotency, pagination, errors, GraphQL, gRPC and when to use each | ADR-005, 006, 010, 011, 057 |
| **Frontend and mobile awareness** | What the client needs: caching, offline, push, token handling | SSE tracking (ADR-020, 055) |
| **Multi-tenancy** | Shared schema, schema per tenant, database per tenant, noisy neighbours | (concept only) |

### 5.3 Data and integration

| Topic | Be able to explain | In Tadka |
|---|---|---|
| **Polyglot persistence** | Which store for which job: relational, key-value, document, search, time series, graph, columnar | Postgres, Redis, Kafka (ADR-034) |
| **Caching** | Cache-aside, write-through, TTL, invalidation, stampede, penetration, avalanche | ADR-018, 019 |
| **Messaging and streaming** | Queue versus log, partitions, ordering, consumer groups, delivery guarantees, lag | ADR-027, Kafka UI |
| **Outbox, inbox, sagas** | Exactly-once effect from at-least-once delivery, compensation | ADR-028, 029, 045 |
| **Consistency models** | Strong, eventual, causal, read-your-writes, CAP and PACELC | Replica lag (ADR-016, 063) |
| **Data pipelines and analytics** | Change data capture, ETL versus ELT, data lake and warehouse, stream processing, OLAP | (concept, interview-level) |
| **Search** | Inverted index, relevance, when a database `LIKE` is enough | (concept, interview-level) |
| **Schema evolution** | Expand and contract, additive-only events | ADR-038, 050 |

### 5.4 Distributed systems theory

| Topic | Be able to explain |
|---|---|
| **Failure is normal** | Partial failure, timeouts, retries, duplicates, reordering |
| **Consensus and leaders** | What Raft or Paxos gives you, what split-brain is, why you rarely write this yourself |
| **Time and ordering** | Clock skew, logical clocks, why "latest write wins" is dangerous |
| **Idempotency** | Why every retry needs it, and how a key is stored in the same transaction (ADR-011) |
| **Backpressure and queues** | A fast front door with a slow queue behind it hides the real bottleneck (Day 16 finding) |

### 5.5 Reliability and performance

| Topic | Be able to explain | In Tadka |
|---|---|---|
| **Resilience patterns** | Timeout, retry with jitter, circuit breaker, bulkhead, fallback, load shedding | ADR-021, 043, 044, 059, 060 |
| **SLI, SLO, error budgets** | How a promise becomes a number and a decision | Day 16 runbook |
| **Capacity and load testing** | Closed versus open load, knee, saturation, soak | `k6/`, `docs/runbooks/day-16.md` |
| **Incident response** | Detection, triage, mitigation, communication, blameless review | `docs/runbooks/` |
| **Disaster recovery** | RPO, RTO, backups, failover, region loss, what you actually test | `scripts/cloud-failover.ps1` |

### 5.6 Security and compliance

| Topic | Be able to explain | In Tadka |
|---|---|---|
| **Identity** | Authentication, authorization, JWT, OAuth2 and OIDC, sessions, refresh tokens, key rotation | ADR-030, 031, 066, 067 |
| **Zero trust** | Every service checks; the gateway is not a wall | ADR-031 |
| **Data protection** | PII classification, masking, encryption at rest and in transit, tokenization, right to be forgotten | ADR-032, 052, 053 |
| **Application security** | OWASP Top 10, input validation, rate limiting, lockout, secrets management | ADR-065, 049 |
| **Compliance** | PCI-DSS, GDPR and India's DPDP Act, audit logs, data residency | ADR-032, 053 |
| **Supply chain** | Dependency scanning, image scanning, signed builds | (concept) |

### 5.7 Cloud, platform and operations

| Topic | Be able to explain | In Tadka |
|---|---|---|
| **Cloud building blocks** | Compute, storage, network, managed databases, queues, identity, regions and zones | `deploy/azure/`, Terraform |
| **Containers and orchestration** | Docker, Kubernetes or managed container platforms, health probes, autoscaling | Azure Container Apps (ADR-064) |
| **Infrastructure as code** | Terraform or Pulumi, state, drift, modules | `terraform/`, `deploy/azure/` |
| **CI/CD and release** | Pipelines, trunk-based development, blue-green, canary, feature flags | ADR-058, 061 |
| **Observability** | Traces, metrics, logs, RED and USE methods, cardinality, alerting on symptoms | ADR-040 to 042, `docker/observability/` |
| **FinOps** | Cost per order, rightsizing, reserved capacity, tagging, budgets and alerts | `docs/cost-model.md`, the budget alert in `cloud-up` |
| **Platform engineering** | Golden paths, internal developer platforms, paved roads | `scripts/dev-cluster.ps1` is a tiny one |

### 5.8 People and organisation

| Topic | Be able to explain |
|---|---|
| **Conway's law** | Systems copy the communication structure of the organisation that builds them |
| **Team Topologies** | Stream-aligned, platform, enabling, complicated-subsystem teams, and cognitive load |
| **Build, buy, or partner** | Total cost, lock-in, exit plan |
| **Technical debt** | Deliberate versus accidental, how to price it, when to pay it |
| **Governance** | Light guardrails and checks instead of heavy approvals |

---

## 6. Tools, by category

Learn the **category** first, then one tool in it. Names below are examples, not endorsements. Check each product's current documentation before you commit, because products change.

| Category | Job it does | Examples |
|---|---|---|
| **Diagramming and modelling** | Draw and keep diagrams | Mermaid, PlantUML, Structurizr, D2, Excalidraw, draw.io, Lucidchart, Miro, FigJam |
| **Docs as code** | Write, review and publish documents with the code | Markdown in git, MkDocs, Docusaurus, Backstage TechDocs, Confluence or Notion for the rest |
| **Decision records** | Keep ADRs and the log | `docs/adrs/` with a template, adr-tools, Log4brains |
| **API design and testing** | Define and check contracts | OpenAPI and Swagger, AsyncAPI, Postman, Bruno, Pact, buf for gRPC |
| **Architecture governance** | Make rules run in the build | NetArchTest, ArchUnit, dependency-cruiser, SonarQube, CodeScene |
| **Cloud and infrastructure** | Build and run environments | Terraform, Pulumi, Bicep, CloudFormation, Docker, Kubernetes, Helm |
| **Databases and data stores** | Store and move data | PostgreSQL, Redis, Kafka, Elasticsearch or OpenSearch, ClickHouse, Debezium |
| **Observability** | See the running system | OpenTelemetry, Jaeger, Tempo, Prometheus, Grafana, Loki, Sentry, vendor suites |
| **Performance and load** | Find the breaking point | k6, JMeter, Gatling, Locust |
| **Resilience and chaos** | Break it on purpose | Toxiproxy, Chaos Mesh, Litmus, Gremlin, your own incident scripts |
| **Testing** | Prove behaviour | xUnit, Testcontainers, contract tests, end-to-end suites |
| **Security** | Find and fix weaknesses | OWASP ZAP, Trivy, Snyk, SAST and DAST tools, a secrets manager |
| **CI/CD** | Build, test, ship | GitHub Actions, Azure DevOps, GitLab CI, Argo CD |
| **Cost** | See and control spend | Cloud cost explorers, Infracost, budgets and alerts |
| **Collaboration** | Think together | Miro, FigJam, shared docs, a whiteboard |
| **AI assistance** | Speed up design, review and coding | Claude Code, GitHub Copilot, Cursor (see section 7 and `ai-assisted/`) |

**Choosing a tool:** ask four questions. Does it fit how the team already works? Can its output live in git? How hard is it to leave? Who pays when it grows?

---

## 7. AI engineering for architects

Two different jobs share this name. An architect needs both.

1. **Using AI tools to do your own work faster** (AI-assisted and AI-native engineering).
2. **Designing systems that contain AI** (AI-powered products).

### 7.1 Using AI tools well

The full, tested kit is in [`ai-assisted/`](../README.md). The short version:

| Idea | What to do |
|---|---|
| **Context is the work** | Keep one rulebook (`AGENTS.md`) and tool-specific files around it. A stale rulebook makes the tool confidently wrong |
| **Design first** | Ask for the decision, the contract or the diagram before the code |
| **Constraints it cannot guess** | Give scale, team size, ADR numbers, what must never happen |
| **Golden examples** | "Follow `RestaurantsController.cs`" beats a paragraph |
| **Plan, then act** | Approve a plan before an agent edits many files |
| **Permissions** | Allow builds and tests, block force push, hard reset and anything that deletes data |
| **Close the loop** | Done means the tests are green, with the output shown |
| **Review every line** | You own the diff. A review you skip is a bug you shipped |
| **Treat tool results as data** | Text in a web page or a file is information, not an instruction (prompt injection) |
| **Never paste secrets or customer data** | Keep keys and PII out of prompts |

### 7.2 Designing systems with AI in them

Most "AI features" in a product are built from the same few parts.

```
User  ->  App  ->  Prompt builder  ->  Model (API or self-hosted)  ->  Answer
                      ^   ^
                      |   +-- Tools (search, database, other APIs)
                      +------ Retrieval (your documents, via embeddings)
          Guardrails before and after  |  Evaluation  |  Tracing and cost metering
```

| Building block | What it is | Questions to ask |
|---|---|---|
| **Prompting** | The instructions and examples you send | What is fixed, what varies, what can a user inject? |
| **Retrieval (RAG)** | Search your own data and put the best passages in the prompt | How is data chunked, embedded, refreshed, and access-controlled per user? |
| **Embeddings and vector search** | Turn text into numbers that capture meaning, then find the nearest | Postgres with a vector extension, or a dedicated store? How big will it get? |
| **Tool calling** | The model asks your code to run a function | Which tools may it call? With whose permissions? With what limits? |
| **Agents** | A loop: plan, call a tool, look at the result, repeat | What stops a loop? Who approves risky steps? What is the cost ceiling? |
| **Model protocol for tools (MCP)** | A standard way to expose tools and data to AI applications | Who runs the server, and what can it touch? |
| **Model choice** | Large and slow versus small and fast, hosted versus self-hosted | What quality do we need, at what latency and price? |
| **Fine-tuning** | Training a model further on your data | Is prompting plus retrieval enough? Fine-tune only when it is not |
| **Guardrails** | Checks on inputs and outputs (policy, PII, format) | What happens when a check fails? |
| **Evaluation** | A test set and scoring for answer quality | What is the golden set, who labels it, how often does it run? |
| **Observability** | Traces of prompts, tool calls, tokens, cost and latency | Can you replay a bad answer and see why it happened? |

### 7.3 The decision tree: prompt, retrieve, or train?

```
Does the model already know enough to answer?
 |-- Yes -> good prompt, examples, structured output
 |-- No, the missing knowledge is in our documents or database
 |        -> retrieval (RAG), with per-user access control
 |-- No, the model must change its behaviour or style a lot
          -> fine-tune, only after prompting and retrieval have failed
```

### 7.4 What makes AI systems different to design

| Concern | Why it is different | What to do |
|---|---|---|
| **Non-determinism** | The same input can give different outputs | Evaluate on many cases, set temperature low where you need repeatability, validate outputs |
| **Cost** | You pay per token, and loops multiply it | Cache, use smaller models where they are enough, cap tokens and steps, meter cost per feature |
| **Latency** | Generation is slow | Stream the answer, precompute where possible, set timeouts, plan a fallback |
| **Failure** | Models time out, refuse, or return nonsense | Retries with limits, a fallback model or a plain non-AI path, human hand-off |
| **Data privacy** | Prompts may carry personal data | Mask PII, choose the data-handling terms, keep residency rules, log carefully |
| **Prompt injection** | Untrusted text can try to give the model orders | Separate instructions from data, give tools least privilege, confirm risky actions, never let model output run as code unchecked |
| **Quality drift** | Models and data change | Run the evaluation set in CI and on a schedule, version prompts like code |
| **Explainability and audit** | Someone will ask why it said that | Log inputs, retrieved sources and tool calls, show sources to the user |
| **Human in the loop** | Some actions should not be automatic | Approvals for refunds, deletes, money and messages to customers |

### 7.5 A worked example (a design exercise, not built in Tadka)

*Idea:* a support assistant that answers "where is my order and why is it late?"

1. **Where it lives:** a new small service, not inside Orders. It reads through the existing APIs, so the boundaries (ADR-008, ADR-022) stay intact.
2. **Data it may see:** only the calling customer's orders, checked with the same ownership rule as the Orders API (a customer reads only their own orders). Retrieval filters by customer id **before** anything reaches the model.
3. **Tools:** `get_order_status(orderId)` and `get_delivery_eta(orderId)`, both read-only. No refund tool without a human approval step.
4. **Prompt safety:** order notes and restaurant names are untrusted text. Put them in a data block, never in the instructions.
5. **Failure plan:** if the model is slow or down, show the plain order-status screen. A correctness path never depends on the model (the dependency classes of ADR-044).
6. **Cost and latency:** a token budget per conversation, a timeout of a few seconds, streaming the reply, a small model for routing and a larger one only when needed.
7. **Measure it:** a golden set of 100 real questions, a pass rate that must not drop in CI, and a dashboard of cost per conversation, p95 latency and the "handed to a human" rate.
8. **Trade-off:** it can answer faster than a human, but a wrong answer about a refund is a trust problem. Start read-only.

### 7.6 Skills to build

- Read a model card and a pricing page without being dazzled.
- Build a small evaluation set for any AI feature **before** you build the feature.
- Know the OWASP guidance for LLM applications.
- Be able to say, in one sentence, why a feature should **not** use AI.

**What you give up:** AI features add a dependency you cannot fully test, a cost that grows with use, and a new attack surface. Use them where the value is clear and the failure is cheap.

---

## 8. Making and defending decisions

### 8.1 The decision table

Never bring one option. Bring a table.

| Option | Cost (rupees or effort) | Risk | Reversible? | Fits our team? | Revisit when |
|---|---|---|---|---|---|
| A. Keep one database, add a replica | Low | Low | Yes | Yes | Writes pass the single-node limit |
| B. Split into per-service databases | Medium | Medium | Hard | Needs practice | A team needs independent releases |
| C. Move to a distributed SQL database | High | High | Very hard | No | Global writes are needed |

Then write one paragraph: what you chose, the number that decided it, and what you give up.

### 8.2 Habits that make decisions better

1. **State the trade-off out loud.** "We gain isolation and lose cheap joins."
2. **Ask for the failure mode.** "How does this break at 2 am, and how far does it spread?"
3. **Put a number on it.** Rupees, milliseconds, people, days.
4. **Name the trigger to revisit.** A decision with no revisit trigger is a belief.
5. **Separate one-way and two-way doors.** Move fast on two-way. Slow down on one-way.
6. **Prefer the boring choice** unless you can name what the exciting one buys you.
7. **Write it down the day you decide.** Memory edits history.
8. **Disagree and commit,** then measure, and be willing to reverse.

### 8.3 Common traps

| Trap | What it looks like | Antidote |
|---|---|---|
| **Resume-driven design** | Kubernetes and Kafka for a team of three | Ask what problem it solves today, with a number |
| **Premature distribution** | Microservices before the domain is understood | Monolith first, split only for a real force (ADR-002) |
| **Big-bang rewrite** | A two-year rewrite with nothing shipping | Strangler fig, ship small |
| **Ivory tower** | Decisions without code contact | Build a proof, read the code, join an on-call week |
| **Analysis paralysis** | Perfect comparison, no decision | Time-box the decision, name who decides |
| **Golden hammer** | Every problem is the tool you just learned | Keep the option-space table honest |
| **Copying a giant** | Using the design of a company a thousand times your size | Start from your own numbers |
| **Hidden coupling** | A shared table, a shared enum, a synchronous call "just this once" | Fitness functions in the build |

---

## 9. Communication and leadership

The best design fails if nobody understands it or trusts it.

| Skill | What it looks like |
|---|---|
| **Writing** | Short sentences, the answer first, numbers instead of adjectives, one idea per paragraph |
| **Explaining to different people** | Engineers want the mechanism. Managers want risk, cost and time. Executives want the decision and the consequence on one page |
| **Presenting a design** | Start with the problem, show the options, recommend one, name the risks, ask for the decision |
| **Facilitating** | Run a workshop (event storming, risk storming) so quiet people are heard |
| **Influencing without authority** | Earn trust with small proofs, give credit, ask questions before giving answers |
| **Saying no** | "Not now, here is what would change my answer", with a number |
| **Reviewing** | Review the decision, not the person. Praise what is good, be specific about what is not |
| **Mentoring** | Teach the reasoning, not just the answer. A good review comment explains why |
| **Handling conflict** | Put both positions in a table, agree on the criteria first, then score |
| **Stakeholder mapping** | List who is affected, who decides, who can block, and what each one cares about |

**The one-page executive summary:** problem in one sentence; the recommendation in one sentence; the three numbers that matter (cost, time, risk); what we need from the reader; what happens if we do nothing.

---

## 10. The architect's operating rhythm

| How often | Activity | Output |
|---|---|---|
| **Daily** | Read pull requests that touch boundaries, answer design questions | Short, specific feedback |
| **Weekly** | Design review hour, check the fitness functions and dashboards | Approved designs, fixed drifts |
| **Fortnightly** | Walk the on-call and incident list | Reliability work added to the plan |
| **Monthly** | Cost review, risk register review, ADR backlog | Cost per order, updated risks |
| **Quarterly** | Roadmap review, load test of the real system, game day | Updated capacity plan, tested recovery |
| **Twice a year** | Technology radar, vendor and contract review | Adopt, trial, assess, hold |
| **Yearly** | Architecture health check against the original goals | What to change in the next year |

---

## 11. Reading list and sources

### Books

| Book | Why |
|---|---|
| *Designing Data-Intensive Applications*, Martin Kleppmann | The best single book on data systems |
| *Release It!*, Michael Nygard | Resilience patterns from real outages |
| *Fundamentals of Software Architecture*, Mark Richards and Neal Ford | A broad, practical introduction to the role |
| *Software Architecture: The Hard Parts*, Ford, Richards, Sadalage, Dehghani | Trade-off analysis for distributed systems |
| *Building Evolutionary Architectures*, Ford, Parsons, Kua | Fitness functions and change |
| *Building Microservices*, Sam Newman | Boundaries and migration |
| *Domain-Driven Design*, Eric Evans, and *Learning Domain-Driven Design*, Vlad Khononov | Modelling the business |
| *Team Topologies*, Skelton and Pais | Teams and architecture together |
| *Accelerate*, Forsgren, Humble, Kim | What makes delivery fast and safe |
| *Site Reliability Engineering* and *The SRE Workbook* | Reliability as a discipline (free online) |
| *The Staff Engineer's Path*, Tanya Reilly, and *Staff Engineer*, Will Larson | Working as a senior technical leader |
| *Enterprise Integration Patterns*, Hohpe and Woolf | Messaging patterns |
| *AI Engineering*, Chip Huyen | Building applications on foundation models |
| *Designing Machine Learning Systems*, Chip Huyen | Production ML |

### Websites to bookmark

martinfowler.com (patterns and articles), c4model.com, arc42.org, microservices.io, the cloud providers' Well-Architected pages and architecture centres, sre.google/books, owasp.org (including its guidance for LLM applications), 12factor.net, adr.github.io, agents.md, and the documentation of the tools you use.

### Habits

Read one postmortem a week (many companies publish them). Read the ADRs of an open-source project you use. Keep a private journal of decisions you made and how they aged.

---

## 12. Checklists

### 12.1 Your first 30 days on a new system

- [ ] Draw the context diagram yourself, then compare it with the team's.
- [ ] Find the three most important quality goals, with numbers.
- [ ] Read the last five ADRs (or ask why there are none).
- [ ] Trace one real request end to end in the tracing tool.
- [ ] Run the system locally and run its tests.
- [ ] Join an on-call rotation or read the last ten incidents.
- [ ] List the top five risks and who owns each.
- [ ] Find the cost per month, and the cost per order or user.
- [ ] Find what is enforced by tests and what is only a rule on a page.
- [ ] Write your first ADR or design doc, even a small one.

### 12.2 Before you approve a design

- [ ] Are there at least three options, with costs?
- [ ] Is the failure mode named, and how far does the damage spread?
- [ ] Is there a measured number, not just an opinion?
- [ ] Is there a revisit trigger?
- [ ] Does it respect the existing boundaries, and is that checked by a test?
- [ ] Is it observable (can we see it break)?
- [ ] Is it secure (who can call it, what data does it touch)?
- [ ] Can we roll it back?
- [ ] Do we have the people and skills to run it at 2 am?

### 12.3 Self-assessment (score each from 0 to 3)

0 = never done it, 1 = done it with help, 2 = done it alone, 3 = could teach it.

| Area | Skill | Score |
|---|---|---|
| Documents | Write an ADR and a design doc that others use | |
| Documents | Write NFRs as testable scenarios | |
| Diagrams | Draw C4 levels 1 to 3 and a sequence diagram | |
| Diagrams | Keep a diagram as code in git | |
| Techniques | Run an event storming or risk storming session | |
| Techniques | Write a fitness function that fails the build | |
| Data | Choose a data store from requirements and defend it | |
| Messaging | Explain outbox, inbox and saga to a new joiner | |
| Reliability | Design timeouts, retries and a breaker without double-charging | |
| Performance | Plan and read a load test, and find the real bottleneck | |
| Security | Threat-model a feature with STRIDE | |
| Cloud | Read and review an infrastructure-as-code change | |
| Cost | Build a cost model and a cost per order | |
| AI | Design a retrieval or agent feature with guardrails and an evaluation set | |
| AI tools | Keep a rulebook that several AI tools follow | |
| People | Present a design to executives on one page | |

Anything scored 0 or 1 that your current role needs is your next learning goal. Pick one, build a small proof, write it down.

### 12.4 Quick reference: which document, diagram or tool?

| If you need to... | Use |
|---|---|
| Record why you chose something | An ADR |
| Get agreement before building | A design doc or RFC |
| Show the whole system to a newcomer | C4 level 1 and 2 |
| Explain one flow in time order | A sequence diagram |
| Define legal moves of an entity | A state machine |
| Define what "fast enough" means | An NFR scenario and an SLO |
| Find where the system breaks | A load test (k6) and the dashboards |
| Know how it fails | A failure-mode table and a game day |
| Find security holes early | A threat model (STRIDE) |
| Make a rule stick | A fitness function in CI |
| Move to a new design safely | Strangler fig or expand and contract |
| Decide with many stakeholders | A decision table and a one-page summary |
| Work faster with AI | A rulebook, a plan, small diffs, green tests |
| Add AI to a product | A retrieval or agent design with guardrails, evals and a fallback |

---

*Where to go next in this folder:* [README.md](README.md) (the field manual with Tadka's patterns), [ADR_INDEX.md](ADR_INDEX.md), [PATTERNS_CATALOG.md](PATTERNS_CATALOG.md), [DIAGRAMS.md](DIAGRAMS.md), [SYSTEM_DESIGN_CHEATSHEET.md](SYSTEM_DESIGN_CHEATSHEET.md), and the AI tools kit in [../README.md](../README.md).
