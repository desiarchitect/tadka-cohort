# D1-3: Cohort Overview Slides

> Markdown slide deck for Day 1 opening. Each `---` is a slide break.
> Present using VS Code Markdown preview or export to reveal.js.

---

## Welcome to the System Design Cohort

### From Monolith to Microservices in 8 Weeks

You'll build **Tadka**, a food delivery platform, from scratch.

Not a tutorial. Not a toy project. A real system that handles real problems.

---

## Developer → Architect: What Actually Changes?

A developer asks: **"How do I build this feature?"**
An architect asks: **"Should we build this? What breaks at 10x scale? What's the cost of being wrong?"**

### Types of Architects You'll Hear About

| Role | Scope | Day-to-Day |
|------|-------|-----------|
| **Application Architect** | One app or service | Tech stack, code structure, patterns |
| **Solution Architect** | End-to-end solution for a business problem | Connects services, APIs, third-party integrations |
| **System/Platform Architect** | Infrastructure and platform | Scalability, reliability, deployment, cross-cutting concerns |
| **Enterprise Architect** | Org-wide technology strategy | Standards, governance, long-term roadmap |

Most senior engineers in Indian startups play the **Solution/System Architect** role without the title.

### What Architects Do That Developers Don't

- Own NFRs (latency, availability, cost) not just features
- Write ADRs: document decisions before writing code
- Make buy-vs-build calls (Redis vs custom cache, Kafka vs SQS)
- Review designs and data models, not just pull requests
- Talk to PMs about trade-offs: "We can do 99.99% uptime, but it costs 4x more"
- Think about what happens when things fail, not just when they work

**This cohort takes you from "I can build features" to "I can design systems that survive production."**

---

## The Roadmap

| Phase | Weeks | What Happens |
|-------|:-----:|-------------|
| **Monolith** | 1–3 | Build the full product as a single .NET API + PostgreSQL |
| **Modular Monolith** | 4 | Add domain events, load test, find the bottleneck |
| **Service Extraction** | 5–6 | Extract Payment and Delivery, add Kafka, build API gateway |
| **Production Readiness** | 7–8 | Deploy to AWS, add observability, chaos testing |

8 weekends. 16 sessions. One codebase that evolves from a simple API to a distributed system.

---

## What You'll Build

**Tadka** is a food delivery platform for Bangalore.

4 user types:
- **Customer** — browses restaurants, orders food, tracks delivery
- **Restaurant Partner** — manages menu, accepts orders, marks food ready
- **Delivery Partner** — gets assigned deliveries, navigates, confirms drop-off
- **Admin** — monitors platform, handles complaints, onboards restaurants

Your architecture will support **1 lakh daily orders** by the end.

---

## What This Cohort is NOT

❌ A framework tutorial (you'll learn .NET patterns, but that's not the goal)

❌ A coding bootcamp (you should already be comfortable writing C# or similar)

❌ "Follow along and type what I type" (you'll make architecture decisions and defend them)

❌ Theory-only lectures (every concept is immediately applied to Tadka)

❌ A place where we copy-paste from Stack Overflow (we use Copilot Agent Mode and understand what it generates)

---

## Tech Stack

You don't need to know all of these today. Each one gets added when you need it.

| Layer | Week 1 | By Week 8 |
|-------|--------|-----------|
| **API** | .NET 10 Web API | 5 microservices behind YARP gateway |
| **Database** | PostgreSQL 16 | Primary + read replica + Redis cache |
| **Messaging** | (none) | Apache Kafka |
| **Deployment** | Docker Compose (local) | ECS Fargate + Terraform + GitHub Actions |
| **Observability** | Console logs | Prometheus + Grafana + Tempo + Loki |
| **Resilience** | (none) | Polly circuit breakers + retry policies |
| **AI Tooling** | GitHub Copilot Agent Mode | Same, but you'll know when NOT to trust it |

---

## Ground Rules

**1. Questions are mandatory.**
If you didn't ask a question, you weren't paying attention. There's no dumb question, only dumb architectures built without questions.

**2. "I don't know" is a valid answer.**
In system design interviews and in real life. The follow-up is always: "but here's how I'd find out."

**3. Copilot is your pair programmer, not your brain.**
Use it aggressively for boilerplate. Question everything it generates. If you can't explain why the code works, you don't own it.

**4. Attendance matters.**
Each week builds on the previous one. Missing Week 3 means you won't understand Week 5.

**5. Ship every weekend.**
By Sunday evening, your API should be running with the week's features working. Not perfect. Working.

---

## Today's Agenda (Day 1, Saturday)

| Time | Duration | What |
|------|:--------:|------|
| Hour 1 | 60 min | This overview + Developer → Architect + GitHub Copilot setup |
| Hour 2 | 60 min | Requirements workshop: FRs, NFRs, asking the right questions |
| Hour 3 | 60 min | Latency numbers + NFR targets + back-of-envelope math for Tadka |

**By the end of today, you'll know:**
- What an architect does differently from a developer
- What Tadka does and who it serves
- What "the system must handle 1 lakh orders" actually means in QPS and storage
- Why we're starting with a monolith (and when we'll stop)

---

## Tomorrow's Agenda (Day 2, Sunday)

| Time | Duration | What |
|------|:--------:|------|
| Hour 1 | 60 min | Architecture Decision Records: .NET 10, Monolith First, PostgreSQL exercise |
| Hour 2 | 60 min | Domain model + 4-step framework + Scaffold Tadka (clone, run, verify) |

**By the end of tomorrow, you'll have:**
- A running Tadka.Api with health endpoint
- PostgreSQL running in Docker
- Your first ADR written
- Domain model understood: Entities, Value Objects, Aggregates

---

## One More Thing

You're not here to learn microservices.

You're here to learn **when a monolith stops being enough** and **what to do about it**.

The architecture decisions matter more than the code. The code is just evidence that your decisions work.

Let's get started.
