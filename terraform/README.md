# Tadka instructor Terraform

Not a student assignment. In the cohort we teach deploy as a black box: ALB routing, hops, blast radius, monthly bill (ADR-039). This folder is a runnable stack for instructors and anyone with their own AWS account.

## Layout

```
terraform/
├── modules/
│   ├── network/    # VPC, public + private subnets
│   ├── edge/       # ALB + HTTPS listener (YARP analogue)
│   ├── service/    # ECS target group + path rule + dedicated RDS per service
│   ├── data/       # shared data primitives (extend as needed)
│   ├── msk/        # Kafka backbone
│   └── cache/      # ElastiCache Redis
└── environments/demo/   # wires 4 services + edge + msk + cache
```

Topology narrative and the black-box teaching notes: [`../deploy/README.md`](../deploy/README.md).

## What it will stand up

- VPC, public subnet for ALB, private subnets for ECS/RDS/MSK
- ALB path rules to four ECS Fargate services (same idea as local YARP)
- Four RDS instances (one per service, ADR-026)
- MSK and ElastiCache
- Rough cost: ~₹20-60k/month (see `docs/cost-model.md`)

## When the code is in place

```bash
cd environments/demo
terraform init
terraform plan    # always plan first; apply costs real money
```

Students: look at outputs in class if we show them. You do not hand-write HCL for a grade.