# ADR-039: Cloud Deployment (Terraform + ECS + ALB / API Gateway) — Taught as a Black-Box

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Tadka now runs as **4 services + a gateway** plus Postgres ×4, Redis, and Kafka — locally via `docker compose`. The natural next question is "how does this run in the cloud?" — load balancing, service discovery, network hops, blast radius, release cadence, and **cost (₹)**. But two hard constraints shape *how we teach it*:

1. **It is not laptop-reproducible.** A real deploy needs a cloud account, IAM, a VPC, and **real money** — we cannot make every student stand up an ECS cluster behind an ALB at the Day-4–7 demo bar, and we cannot verify a live cloud deploy in this cohort's environment.
2. **The Terraform-distraction trap (CTO review #2).** Handing students raw HCL to run reliably burns sessions on "my IAM state is broken" Level-1 AWS support instead of on *architecture*. The lesson is the **shape and the results** (how requests route, where the hops are, what it costs), not HCL syntax.

## Decision

Teach cloud deployment as a **black-box: results, not HCL.** Concretely:
- **Model**: each service is a container → an **ECS (Fargate) task/service**; an **Application Load Balancer (ALB)** does L7 routing + health checks + per-service target groups; an **API Gateway**/edge sits in front for auth/throttling/WAF; **CloudFront/CDN** for static assets; managed **RDS Postgres**, **ElastiCache Redis**, **MSK (Kafka)**. IaC via **Terraform** (modules per service).
- **Taught via**: a topology diagram + a **read-only sample Terraform/ECS+ALB template** shipped under `tadka/deploy/` as *reference only* (not a run-in-session step), and a "magic-button" path (`terraform apply` / a CDK stack) for the rare student **with** an account who wants the real thing on their own time.
- **In-session focus**: read the **results** — ALB request routing to target groups, the extra network hop the gateway adds, auto-scaling per service, blast radius per task, and a **cost model in ₹/month** (the same $250–270 budget framing). We analyze the running shape; we do not debug HCL live.
- **The YARP gateway (ADR-035) stays the runnable local stand-in** for the edge — students get the real routing/rate-limit feel on a laptop; the cloud ALB/API-Gateway is the production mapping of that same role.

## Consequences

### Positive
- Students learn the cloud **architecture and economics** without a cloud bill or an IAM rabbit hole.
- Session time stays on architecture (routing, hops, blast radius, cost), per the CTO guardrail.
- Account-holders still get a real, deployable reference.

### Negative / Risks
- No hands-on "I deployed it live" for most students (mitigated: the local YARP + compose stack is the hands-on; the cloud is the map).
- A sample template can drift from cloud-provider changes — it's reference, dated, not a maintained product.

### Cost (₹ / effort)
Authoring the diagram + sample template + cost model — modest. Running it: **₹0 for the cohort** (black-box); the real deploy's cost (~₹20–60k/month at this shape, dominated by RDS/MSK/NAT) is itself a teaching artifact.

## Alternatives Considered

### Option A: Hands-on Terraform for everyone
- Pros: real muscle memory.
- Cons: cloud account + $ + IAM/VPC debugging eats sessions; not reproducible at the demo bar.
- Why rejected: the CTO "Terraform distraction" trap; not laptop-reproducible.

### Option B: Skip cloud entirely (compose is enough)
- Pros: simplest.
- Cons: students never connect the architecture to LB/hops/blast-radius/cost — the Staff-level questions.
- Why rejected: the *analysis* (results) is core curriculum even if the *apply* is not.

### Option C: Kubernetes (EKS) instead of ECS
- Pros: portable, the industry lingua franca.
- Cons: far more concepts (control plane, ingress, operators) for the same teaching goal at this scale.
- Why rejected *for the default path*: ECS/Fargate is the smaller cognitive load to show the shape; k8s is **named** in the option-space as the "when you outgrow ECS / want portability" choice.

## Teaching fields

- **Topic:** How deployment shape determines routing, network hops, blast radius, release cadence, and cost — taught as **results, not HCL**.
- **Options:** hands-on Terraform-for-all · skip cloud · **black-box results + reference template (chosen)** · EKS vs ECS.
- **Choice:** ECS/Fargate + ALB + API-Gateway + managed RDS/Redis/MSK, Terraform reference template read-only; analyze the running shape + ₹ cost; YARP stays the local edge.
- **Why:** not laptop-reproducible + the Terraform-distraction trap; the architecture/economics are the lesson, not the syntax.
- **Trade-off:** no universal hands-on cloud apply; reference template can date.
- **Failure mode** (2 AM dinner rush): a single-AZ ALB or one ECS task with no auto-scaling → an AZ blip or a traffic spike takes the whole front door down. The teaching point: multi-AZ + per-service auto-scaling + health checks = blast-radius control — read from the topology, not from HCL.
- **Revisit when:** the org standardizes on Kubernetes (→ EKS + ingress) or serverless (→ Lambda/API-Gateway for spiky services); or when multi-region/DR enters scope (Week 8+).
- **Cross-stack equivalents:** the deployment model is language-neutral — any container (Spring Boot jar / Node / Go binary) is an ECS task / k8s pod the same way. Terraform ≈ AWS CDK / Pulumi / CloudFormation; ECS ≈ Cloud Run / Azure Container Apps / k8s Deployment; ALB ≈ GCP HTTPS LB / Azure App Gateway / nginx-ingress.

## References
- ADR-035 (YARP gateway — the local stand-in for the cloud edge), ADR-026 (database-per-service → managed RDS per service), CTO review #2 (Terraform-distraction guardrail; results-not-HCL)
- `cohort-prep/day-12/option-space.md` (deploy-options matrix + ₹ cost), `tadka/deploy/README.md` (black-box reference)

## Revisit When
When the cohort adds a dedicated deploy/SRE module, or when a real production target is chosen — at which point the sample template becomes a maintained, environment-specific artifact rather than read-only reference.
