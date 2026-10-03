# Day 12: Tadka on Azure (diagrams)

Every Azure component that `scripts/cloud-up.ps1` creates, drawn from [`deploy/azure`](../../deploy/azure/README.md). The live session runs **basic** on Day 12 and Day 16, and **ha** on Day 14. Hosting is **Azure Container Apps**, not AKS (ADR-064). The local version of the same system is in [`day-12-topology.md`](day-12-topology.md).

Deploy, timings and cost: [`docs/runbooks/cloud-deploy.md`](../runbooks/cloud-deploy.md).

## Basic mode (Day 12, Day 16)

The cheapest full system: one of everything, one Postgres server with four databases, one Redis container. Gateway and api autoscale from 1 to 5 replicas at 30 concurrent requests each.

![Tadka on Azure, basic mode](day-12-azure-basic.svg)

## HA mode (Day 14)

Same topology. Orange outlines mark what changes: Postgres becomes General Purpose with a zone-redundant standby and a read replica, and Redis becomes two nodes watched by three Sentinels.

![Tadka on Azure, HA mode](day-12-azure-ha.svg)

**Legend:** purple = Front Door (global edge) · green = container app · blue = Postgres Flexible Server · beige = monitoring · grey = secrets, DNS, budget · white = outside Azure · orange outline = changed in HA. Dashed arrows: SSE straight to the gateway, image pull from GHCR, async replication, Private DNS link.

## One order, end to end

1. The browser calls the Front Door endpoint. The WAF counts requests per client IP and blocks above 3,000 a minute. `GET /api/v1/restaurants*` can be answered from the edge cache for 30 seconds (`x-cache: TCP_HIT`).
2. Front Door forwards to the gateway over HTTPS with its `X-Azure-FDID` header. The gateway refuses anything without it (403), except `/health` and the SSE stream.
3. YARP routes by path: payments to `payment`, deliveries to `delivery`, restaurants to `restaurant`, everything else to `api`. Container Apps' built-in Envoy balances across replicas, and sits between the gateway and each service too.
4. Each service writes its own database on the Postgres server over a private IP, and publishes events through its Outbox to `kafka`. The other services consume them.
5. Every app sends traces and metrics over OTLP to `otel-collector`, which exports to Application Insights.
6. Live order tracking (SSE) skips Front Door and connects to the gateway URL directly, because Front Door cuts long-lived responses.

## Local box to Azure box

| On the laptop (docker compose + `dotnet run`) | On Azure |
|---|---|
| `Tadka.Gateway :8080` | `gateway` container app, the only public ingress |
| Monolith, Payment, Delivery, Restaurant | `api`, `payment`, `delivery`, `restaurant` container apps, internal only |
| `postgres`, `payment-db`, `delivery-db`, `restaurant-db` (4 containers) | **one** Postgres Flexible Server, 4 databases, no public access |
| `kafka`, `redis` containers | `kafka`, `redis` container apps (HA: `redis-a`, `redis-b`, 3 Sentinels) |
| Day 6 menu cache in Redis | still there, plus a 30 s edge cache in Front Door |
| Nothing (localhost) | Front Door + WAF, VNet with two private subnets, Private DNS zone |
| Console logs, local OTEL | `otel-collector` → Application Insights + Log Analytics |

## Component list

| Component | Terraform resource | Basic | HA |
|---|---|---|---|
| Resource group | `azurerm_resource_group` | `rg-tadka-session`, Central India | same |
| Front Door profile and endpoint | `cdn_frontdoor_profile`, `_endpoint` | Standard, `afd-tadka`, `*.azurefd.net` | same |
| Origin group, origin, route | `cdn_frontdoor_origin_group`, `_origin`, `_route` | One origin: the gateway, HTTPS only, all paths | same |
| Cache rule | `cdn_frontdoor_rule_set`, `_rule` | `/api/v1/restaurants*` cached 30 s | same |
| WAF | `cdn_frontdoor_firewall_policy`, `_security_policy` | Prevention, 3,000 req/min per IP; 60,000 with `-LoadTest` | same |
| Virtual network | `virtual_network`, `subnet` ×2 | `10.40.0.0/16`; Container Apps `/23`; Postgres `/24` | same |
| Container Apps environment | `container_app_environment` | `cae-tadka`, Consumption profile, external ingress | same |
| gateway, api | `container_app` | 0.5 vCPU, 1 GiB; HTTP autoscale 1 to 5 | same |
| payment, delivery, restaurant | `container_app` | 0.5 vCPU, 1 GiB; 1 replica (payment 1 to 4 on Kafka lag with `-KafkaScaling`) | same |
| Kafka | `container_app` | `apache/kafka:3.8.0` KRaft, 1 vCPU, 2 GiB, TCP 9092, 16 topics auto-created | same |
| Redis | `container_app` | One `redis:7.2-alpine`, 0.25 vCPU, TCP 6379 | **redis-a, redis-b and sentinel-1 to 3, quorum 2** |
| OTEL Collector | `container_app` | `otel-collector-contrib:0.115.1`, 0.5 vCPU | same |
| Postgres server | `postgresql_flexible_server` | v16, `B_Standard_B1ms`, zone 1, 32 GiB, 7-day backups, no public access | **`GP_Standard_D2ds_v5`, zone-redundant standby in zone 2** |
| Postgres read replica | `postgresql_flexible_server` (Replica) | none (B1ms cannot have one) | **zone 3, async; api reads use it** |
| Databases | `postgresql_flexible_server_database` ×4 | `tadka`, `tadka_payment`, `tadka_delivery`, `tadka_restaurant` | same |
| Private DNS | `private_dns_zone`, `_virtual_network_link` | `tadka-*.private.postgres.database.azure.com` | same |
| Monitoring | `log_analytics_workspace`, `application_insights` | Container logs and OTEL telemetry | same |
| Budget | `consumption_budget_resource_group` | 1,000 (billing currency); email at 50% actual, 100% forecast | same |
| Generated secrets | `random_password`, `tls_private_key`, `random_bytes` | DB password, RS256 JWT key, PII encryption key | same |
| Azure Managed Redis | `managed_redis` | not created | **only with `-WithManagedRedis`; `Balanced_B0`, for comparison, apps don't use it** |

## Considered and not used

- **AKS.** Control plane, ingress controller, node pools and Helm for the same teaching result, and nodes bill while idle (ADR-064, option C).
- **Event Hubs.** Tadka has 16 topics; Event Hubs Standard allows 10 per namespace.
- **Azure Cache for Redis.** Stops new-cache creation from 1 Oct 2026. Managed Redis has no failover you can trigger on cue, so Day 14 uses Sentinel.
- **Application Gateway or Load Balancer.** The Container Apps environment's Envoy ingress already load balances.
- **Azure Container Registry and Key Vault.** Images come from GHCR, built by the `images` workflow. Secrets are generated by Terraform and stored as Container App secrets.

## Scaling, for when students ask

Only `gateway` and `api` autoscale (HTTP rule: one more replica per 30 concurrent requests, up to 5). The live demo of it is Day 16: k6 against the Front Door URL while `az containerapp replica list -g rg-tadka-session -n api -o table` shows 1 → 5 → 1.
