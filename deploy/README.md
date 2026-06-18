# Tadka — Cloud Deployment (read-only reference, taught as a black-box)

> **This is reference, not a run-in-class step (ADR-039).** Backend architects shouldn't spend the
> weekend fighting AWS IAM. Read the **topology and the bill**; don't hand-write HCL. The runnable edge
> on your laptop is the **YARP gateway** (`src/Tadka.Gateway`, ADR-035) — that's the production edge's
> local stand-in. The template below is illustrative; an account-holder can adapt it on their own time.

## The shape: 4 services + a gateway, in the cloud

```
                       ┌────────────────────────── AWS account / VPC ──────────────────────────┐
  client ──HTTPS──▶  API Gateway / ALB  (L7 routing, health checks, TLS, edge rate-limit, WAF)
                       │   /api/v1/orders,/auth,/users  ─▶  ECS service: monolith  ─▶ RDS (ordering/identity)
                       │   /api/v1/payments             ─▶  ECS service: payment   ─▶ RDS (payment)
                       │   /api/v1/deliveries           ─▶  ECS service: delivery  ─▶ RDS (delivery) + ElastiCache (geo)
                       │   /api/v1/restaurants          ─▶  ECS service: restaurant─▶ RDS (restaurant) + ElastiCache (menu cache)
                       │   async backbone               ─▶  MSK (Kafka)  ◀─ all services
                       └───────────────── public subnet: ALB · private subnets: ECS + RDS + MSK ─────────────────┘
   static assets ──▶ CloudFront (CDN)
```

## Read the results, not the syntax
- **Routing:** the ALB maps each path prefix to a **target group** (one per ECS service) — the same routing YARP does locally.
- **Network hops:** client → ALB → service → RDS. **Count them.** Each hop adds latency; the gateway adds one. Cross-service calls (e.g. a sync read) add more — which is *why* Ordering keeps a local read model (ADR-037) instead of calling Restaurant on the order path.
- **Blast radius:** public ALB, **private** ECS + RDS + MSK (no public DB). Multi-AZ + per-service auto-scaling = a single AZ blip or one task dying doesn't take the front door down.
- **Cost (₹, order-of-magnitude):** ~**₹20–60k/month** at this shape, dominated by **RDS (×4)**, **MSK**, and **NAT gateways** — *not* by the compute. The architecture lesson: a per-service managed DB is the expensive part of database-per-service; budget for it before you split.

## Illustrative Terraform (reference — do NOT walk line-by-line in class)
```hcl
# One reusable module per service: ECS Fargate service + task + target group + RDS.
module "restaurant" {
  source         = "./modules/service"
  name           = "restaurant"
  image          = "${var.ecr_repo}/restaurant:${var.tag}"
  container_port = 8080
  desired_count  = 2                       # multi-task → blast-radius control
  vpc_id         = module.network.vpc_id
  private_subnets = module.network.private_subnets
  alb_listener   = module.edge.https_listener_arn
  route_prefix   = "/api/v1/restaurants"   # ALB rule → this service's target group
  db_instance    = "db.t3.small"           # its OWN RDS (database-per-service, ADR-026)
}
# ...repeat for monolith / payment / delivery; module.edge = ALB + ACM + WAF; module.msk = Kafka.
```

## Instructor Terraform (`../terraform/`)

Runnable IaC for people with an AWS account is in **[`../terraform/`](../terraform/)**: VPC, ALB, ECS Fargate (4 services), RDS (4), MSK, ElastiCache. Students do not write or submit this. In class we use the topology diagram, hop count, and monthly bill. Maybe a `terraform output` screenshot. Not a line-by-line HCL walk (ADR-039).

## Magic button (instructor or self-study, not a student assignment)
```
cd terraform/environments/demo && terraform init && terraform plan   # check before you spend
# terraform apply   # real money — tear down after
```
Care about what comes back: ALB DNS, health checks, request flow, the bill. Not the HCL. CDK or CloudFormation is fine too if you prefer.

## Why this is a black-box in the cohort
- Not laptop-reproducible (needs an account, IAM, a VPC, **real money**) — can't meet the Day-4–7 demo bar here.
- The "Terraform distraction" trap: a student changes one variable, breaks IAM state, and a session becomes Level-1 AWS support. Keep the focus **architectural**.
- **Kubernetes (EKS)** is the alternative when you outgrow ECS / want portability — named, not built (more concepts for the same teaching goal at this scale).
