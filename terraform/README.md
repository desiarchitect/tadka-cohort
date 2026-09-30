# Tadka AWS reference Terraform (plan-only)

Not a student assignment, and **never applied in the cohort**. The live class deploy is Azure
([`../deploy/azure/`](../deploy/azure/), ADR-064). This folder is the honest AWS picture of the same
architecture, complete enough for `terraform validate` and `terraform plan`, for instructors and anyone
reading the AWS shape. Topology and the AWS ↔ Azure map: [`../deploy/README.md`](../deploy/README.md).

> **Status:** written without Terraform available in the authoring environment. It has not yet been through
> `terraform validate`/`plan`; do that before relying on it.

## Layout

```
terraform/
├── modules/
│   ├── network/    # VPC, 2 public + 2 private subnets, internet gateway, ONE NAT gateway, route tables
│   ├── edge/       # ALB; HTTPS:443 if certificate_arn is set, else HTTP:80 behind CloudFront
│   ├── cdn/        # CloudFront in front of the ALB; 30 s cache for GET /api/v1/restaurants*
│   ├── platform/   # ECS cluster, task execution role, log group, Cloud Map (tadka.local), internal SG,
│   │               # shared secrets (RSA JWT signing key for the monolith, PII key) as SSM SecureStrings from tls_private_key / random_bytes
│   ├── registry/   # 5 ECR repositories (api, payment, delivery, restaurant, gateway)
│   ├── service/    # ONE ECS Fargate service: task definition + service + optional ALB target group/rule
│   │               # + optional own RDS (generated password, connection string in SSM) + CPU autoscaling
│   ├── cache/      # ElastiCache Redis
│   ├── msk/        # managed Kafka, only when enable_msk = true
│   └── data/       # intentionally empty (kept for layout compatibility)
└── environments/demo/   # wires it all: 4 services + gateway + Kafka + Redis + edge + CDN
```

## What `plan` shows

- VPC with working routes: public subnets → internet gateway, private subnets → one NAT gateway
  (one, not one per AZ: cheaper, but an AZ-level egress single point of failure).
- CloudFront → ALB. The ALB routes `/api/v1/orders`, `/payments`, `/deliveries`, `/restaurants` straight to
  those ECS services and everything else (auth, users, health, demo) to the **gateway** service.
- Four ECS Fargate services + the gateway, in private subnets, each with a target-tracking CPU autoscaling
  policy (ordering and gateway up to `max_count`, others up to 2).
- Four RDS Postgres instances (database-per-service, ADR-026), passwords from `random_password`, full
  connection strings in SSM Parameter Store and injected by ECS (no secrets in task definitions).
- Kafka: one self-hosted KRaft broker as an ECS task by default. `enable_msk = true` swaps in MSK
  (2 brokers), the single most expensive line on the modeled bill, bought only when you need broker
  replication and a managed SLA.
- ElastiCache Redis, 5 ECR repositories, CloudWatch logs.
- Optional `certificate_arn` for an HTTPS ALB listener. Without it, CloudFront terminates TLS and talks
  HTTP to the ALB (fine for a reference; production puts TLS on both legs).

Not built (named as upgrades): AWS WAF, a migration job separate from app startup, per-tier security groups,
multi-NAT, Secrets Manager rotation.

**Cost:** modeled at **~₹31,300/month** for the built shape in [`../docs/cost-model.md`](../docs/cost-model.md)
(with MSK). This stack adds an unpriced NAT gateway and, by default, replaces MSK with one Fargate task.
It is a model, not a bill.

## Running plan

```bash
cd environments/demo
terraform init
terraform validate
terraform plan        # needs AWS credentials for data sources (AZs, CloudFront managed policies)
# terraform apply     # NOT part of the cohort. Real money; images must be pushed to ECR first.
```

Students: look at the outputs and the map in class if we show them. You do not hand-write HCL for a grade.
