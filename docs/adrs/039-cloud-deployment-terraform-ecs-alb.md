# ADR-039: Cloud Deployment (Terraform + ECS + ALB + API Gateway)

**Date:** 2026-06-05
**Status:** Accepted
**Deciders:** Tadka Engineering Team

## Context

Tadka consists of 4 services, an API gateway, 4 PostgreSQL databases, Redis, and Kafka. As we move from local development (`docker compose`) to a production cloud environment, we need to define our target deployment architecture. The natural questions involve load balancing, service discovery, network hops, blast radius, release cadence, and **cost**. 

We have two hard constraints shaping our deployment strategy:
1. **Operational overhead vs. Control:** We are a lean engineering team. We cannot afford to dedicate multiple full-time engineers to manage a complex orchestration control plane.
2. **Developer Experience (The "IaC Distraction" Trap):** We want product engineers focused on business logic and architecture, not debugging IAM roles, VPC subnets, or raw HCL syntax. The platform should abstract the boilerplate so teams focus on the *shape and results* of their deployments (routing, cost, blast radius).

## Decision

We will deploy our services using **Amazon ECS (Fargate)** with **Terraform** managed by a central platform approach. Concretely:

- **Compute Model**: Each service is built as a container and deployed as an **ECS (Fargate) task/service**. An **Application Load Balancer (ALB)** handles L7 routing, health checks, and per-service target groups.
- **Edge Layer**: An **API Gateway** sits in front for auth, throttling, and WAF. **CloudFront/CDN** is used for static assets.
- **Stateful Services**: We will use managed services rather than self-hosting: **RDS Postgres**, **ElastiCache Redis**, and **Amazon MSK (Kafka)**.
- **Infrastructure as Code**: The platform team will maintain standard **Terraform** modules. Product teams will consume these modules as a "black box" abstraction. They will supply standard inputs (CPU, RAM, env vars) rather than writing raw HCL, preventing configuration drift and security misconfigurations.
- **Local Dev vs Cloud**: YARP gateway remains our local edge abstraction, matching the routing and rate-limiting behavior of the cloud API Gateway/ALB, ensuring developer environments accurately reflect production topologies.

## Consequences

### Positive
- **Low Operational Burden:** Fargate and managed stateful services eliminate OS patching and node management.
- **Developer Focus:** Standardized Terraform modules prevent engineers from falling down IAM/VPC rabbit holes. Teams focus on architecture and economics.
- **Clear Routing & Blast Radius:** Dedicated ALBs and isolated ECS tasks provide a clear topology with isolated failure domains.

### Negative / Risks
- **Platform Team Bottleneck:** Changes to the underlying infrastructure capabilities require updates to the central Terraform modules.
- **Vendor Lock-in:** Heavy reliance on AWS managed services (ECS, MSK, API Gateway).

### Cost
The managed database tier (RDS, MSK, NAT Gateways) will dominate our initial AWS bill. The compute tier (Fargate) scales linearly with load. The cost modeling is transparently shared with product teams so they understand the financial impact of adding new services.

## Alternatives Considered

### Option A: Product Teams Write Raw Terraform
- **Pros:** Maximum flexibility for each team.
- **Cons:** High risk of security misconfigurations, fragmented IAM/VPC setups, and lost engineering cycles debugging HCL.
- **Why rejected:** The "Terraform distraction" trap; we want consistency and speed.

### Option B: Kubernetes (Amazon EKS)
- **Pros:** Highly portable, the industry standard for large-scale container orchestration, rich ecosystem.
- **Cons:** Significant cognitive load (control plane, ingress controllers, operators, Helm) which is overkill for 4 services.
- **Why rejected:** ECS/Fargate offers a much smaller cognitive load to achieve the exact same operational goals at our current scale. EKS is reserved as a future migration path if we outgrow ECS or require extreme multi-cloud portability.

### Option C: EC2 VMs with Docker Compose
- **Pros:** Mirrors local development exactly.
- **Cons:** Zero auto-scaling, manual instance management, no built-in blast-radius control.
- **Why rejected:** Lacks the resilience, auto-scaling, and managed health checks required for production workloads.

## Implementation Notes

- **Resilience:** Services must span multiple AZs, with auto-scaling policies tied to CPU/memory metrics. Health checks must be configured at the ALB level to ensure traffic is only routed to healthy tasks.
- **Failure Mode:** A misconfigured single-AZ deployment or a hardcoded single ECS task means an AZ blip or traffic spike takes the service down. Multi-AZ + auto-scaling + health checks are mandatory parameters in the Terraform modules.
- **Cross-stack equivalents:** This containerized deployment model is language-neutral. Any container (Spring Boot jar / Node / Go binary) maps to an ECS task. The infrastructure logic is conceptually similar to using GCP Cloud Run + HTTPS LB, or Azure Container Apps.

## Revisit When
We outgrow the capabilities of ECS (e.g., needing complex service mesh routing, custom operators, or reaching a scale of 50+ microservices), at which point a migration to EKS will be evaluated.
