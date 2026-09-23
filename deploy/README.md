# Tadka — Cloud Deployment

Two cloud stacks, one architecture (ADR-064, which supersedes part of ADR-039):

| | **Azure** ([`azure/`](azure/)) | **AWS** ([`../terraform/`](../terraform/)) |
|---|---|---|
| Role | **Live, per class session.** Up before class, down after. | **Reference only.** `terraform plan`, never applied in the cohort. |
| Used on | Day 12 (`basic`), Day 14 (`ha`), Day 16 (`basic`) | Reading the AWS shape; mapping boxes |
| Run with | `scripts/cloud-up.ps1` / `cloud-down.ps1` / `cloud-failover.ps1` | `terraform plan` in `terraform/environments/demo` |
| Runbook | [`docs/runbooks/cloud-deploy.md`](../docs/runbooks/cloud-deploy.md) | [`../terraform/README.md`](../terraform/README.md) |
| Your own copy (optional) | [`docs/runbooks/self-deploy.md`](../docs/runbooks/self-deploy.md): your free Azure account, cost warning, always run `cloud-down` | n/a |

> **Students: results, not HCL.** You hit the public URL, watch the CDN hit, the autoscale, the failover and
> the bill. You never write or submit Terraform. The runnable edge on your laptop is still the **YARP gateway**
> (`src/Tadka.Gateway`, ADR-035) behind `docker compose`.

## The shape: 4 services + a gateway, in the cloud

```
  client ──HTTPS──▶ CDN + WAF (Front Door | CloudFront)
                        │
                        ▼
                   load balancer (Container Apps ingress | ALB)
                        │
                        ▼
                   gateway (YARP) ──▶ monolith/Ordering ─▶ Postgres (tadka)
                                  ├─▶ Payment          ─▶ Postgres (tadka_payment)
                                  ├─▶ Delivery         ─▶ Postgres (tadka_delivery) + Redis (geo)
                                  └─▶ Restaurant       ─▶ Postgres (tadka_restaurant) + Redis (menu cache)
                   Kafka (async backbone) ◀─ all four services
                   OTEL Collector ─▶ Application Insights | (CloudWatch / any OTLP backend)
```

## AWS ↔ Azure component map

Map each box to its `docker-compose` service and to both clouds. The architecture is the same; only the
product names change.

| Concern | Local (`docker compose` / `dotnet run`) | Azure live (`deploy/azure`) | AWS reference (`terraform/`) |
|---|---|---|---|
| CDN + TLS at the edge | none (plain http) | Front Door Standard, `*.azurefd.net` managed cert | CloudFront, default `*.cloudfront.net` cert |
| WAF / edge rate limit | gateway fixed-window limiter | Front Door WAF custom rate-limit rule, 3000/min per IP (`cloud-up -LoadTest` raises it for Day 16; managed rules need Premium) | not built (AWS WAF on CloudFront is the upgrade) |
| Load balancer | nginx (`scale-out` profile) | Container Apps built-in Envoy ingress (no separate resource) | Application Load Balancer + target groups |
| API gateway | `Tadka.Gateway` :8080 | `gateway` container app, the only external ingress | `gateway` ECS service, the ALB's catch-all rule |
| Services | `dotnet run` x4 | 4 container apps, internal ingress | 4 ECS Fargate services in private subnets |
| Service discovery | `localhost:<port>` | `http://<app-name>` inside the environment | Cloud Map `<name>.tadka.local` |
| Autoscaling | none | HTTP concurrency rule, gateway + api, 1..5 (optional: Kafka-lag rule on Payment, 1..4) | `aws_appautoscaling_*` target tracking on CPU |
| Postgres | 4 containers (5432, 5434-5436) + replica (5433) | 1 Flexible Server, 4 databases, private access (no public endpoint); `ha`: zone-redundant HA + read replica | 4 RDS instances (one per service), generated passwords |
| Realtime (SSE) | gateway :8080 | gateway URL directly, not through Front Door (the CDN cuts long-lived responses) | not split in the reference (same pattern: a realtime hostname on the ALB that skips CloudFront) |
| Origin lockdown | none | gateway 403s requests without our `X-Azure-FDID` (except health probes and SSE) | not built (CloudFront custom header or AWS-managed prefix list is the equivalent) |
| Redis | `redis` container | Redis container; `ha`: primary + replica + 3 Sentinels | ElastiCache (1 node) |
| Kafka | KRaft container | KRaft container app (14 topics > Event Hubs Standard's 10) | KRaft ECS task by default; MSK behind `enable_msk` |
| Observability | OTEL Collector → Jaeger/Prometheus/Grafana (`observability` profile) | OTEL Collector → Application Insights | CloudWatch Logs (+ any OTLP backend) |
| Secrets | `appsettings.Development.json` | `random_password` → Container App secrets | `random_password` → SSM Parameter Store SecureString |
| Images | built locally | GHCR (`.github/workflows/images.yml`) | ECR repositories (5) |
| Network | host | VNet-integrated environment (needed for TCP ingress) | VPC, public + private subnets, IGW, one NAT, route tables |

## Read the results, not the syntax
- **Routing:** one public entry. On Azure only the gateway is public and it routes by path (the same YARP
  config as local). On AWS the ALB routes the four service prefixes itself and sends everything else to
  the gateway: both patterns, side by side.
- **Network hops:** on Azure the path is client → Front Door → Envoy → gateway → **Envoy** → service →
  Postgres: 7 boxes, **6 hops**. The second Envoy is the hidden one: inside Container Apps, service-to-service
  calls also go through the platform's ingress proxy. **Count them.** Each hop adds latency, which is *why*
  Ordering keeps a local read model (ADR-037) instead of calling Restaurant on the order path.
- **Scale signal:** AWS here scales on CPU, Azure on HTTP concurrency (requests in flight per replica). The
  scale signal is a design choice, not a default: CPU suits compute-bound work, concurrency suits I/O-bound
  APIs that wait on a database (Tadka's), and queue lag suits consumers (the optional Payment Kafka rule).
- **Blast radius:** only the edge is public; services, databases, Redis and Kafka are internal.
- **Cost:** the AWS "built" shape is modeled at **~₹31,300/month** in
  [`docs/cost-model.md`](../docs/cost-model.md), dominated by **4 managed databases** and **Kafka (MSK)**,
  not by compute. The AWS Terraform also has a **NAT gateway** that the model does not price, and runs Kafka
  as one task unless `enable_msk = true`. The Azure live sessions are billed by the hour and destroyed after
  class; their **real** bills are recorded in the same file.

## Why the cohort deploys live now (and why only per session)
- A screenshot of `terraform output` taught nothing a diagram didn't. A public URL with a CDN hit, an
  autoscale and a real failover does.
- Per session, not 24x7: three sessions of ~4 hours cost a small fraction of one always-on month.
- **Kubernetes (AKS/EKS)** stays named, not built: more concepts for the same teaching result at this scale.
