# Tadka on Azure: the live per-session deploy (ADR-064)

Instructor reference. Students see the **results** (a public URL, a CDN hit, autoscaling, a failover, a real
bill), not this HCL. Run it through the scripts, not by hand:

```powershell
./scripts/cloud-up.ps1 -Mode basic -AlertEmail you@example.com   # Day 12, Day 16
./scripts/cloud-up.ps1 -Mode ha    -AlertEmail you@example.com   # Day 14 failover
./scripts/cloud-failover.ps1 -Target db -Kind forced             # Day 14 only
./scripts/cloud-down.ps1                                         # after EVERY session
```

Prereqs, timings, costs and the per-session checklist: [`docs/runbooks/cloud-deploy.md`](../../docs/runbooks/cloud-deploy.md).

> **Status: `terraform validate` passes (checked with Terraform 1.16.2), but nothing has been through `plan` or
> `apply` against a real Azure subscription yet.** The first real run is the real test. Expect to fix small
> schema, quota or naming details.

## What it builds (one resource group, region `centralindia`)

```
 student ──HTTPS──▶ Front Door Standard (CDN + WAF rate-limit + TLS, *.azurefd.net)
                        │  caches GET /api/v1/restaurants* for 30 s (x-cache: TCP_HIT)
                        ▼
              ┌──────────── Container Apps environment (VNet-integrated, built-in Envoy LB) ────────────┐
              │  gateway (YARP)  ◀── the ONLY external ingress; HTTP scale rule 1..max_replicas          │
              │     ├─▶ api         (internal)  HTTP scale rule 1..max_replicas                         │
              │     ├─▶ payment     (internal)                                                          │
              │     ├─▶ delivery    (internal)                                                          │
              │     └─▶ restaurant  (internal)                                                          │
              │  kafka (KRaft, TCP 9092) · redis (TCP) or redis-a/b + sentinel-1..3 · otel-collector     │
              └──────────────────────────────────────────────────────────────────────────────────────────┘
                        │                                              │
                        ▼                                              ▼
       Postgres Flexible Server (1 server, 4 DBs)          Application Insights + Log Analytics
       ha: + zone-redundant standby + read replica
```

| Concern | `basic` | `ha` (Day 14 only) |
|---|---|---|
| CDN / WAF / TLS | Front Door Standard, custom rate-limit rule | same |
| Load balancer | Container Apps built-in ingress (no LB resource) | same |
| Gateway | `gateway` container app, external ingress | same |
| Services | 4 internal container apps, min 1 replica | same |
| Autoscaling | HTTP concurrency rule on gateway + api (1..`max_replicas`, default 5) | same |
| Postgres | Flexible Server **B1ms**, 4 databases, read string = primary | **GP D2ds_v5**, zone-redundant HA, **+ read replica** wired to `TadkaDbReplica` |
| Redis | one Redis container | **redis-a + redis-b + 3 Sentinels** (quorum 2) |
| Kafka | KRaft container app (14 topics > Event Hubs Standard's 10) | same |
| Observability | OTEL Collector → Application Insights | same |
| Secrets | `random_password` → Container App secrets | same |
| Budget alert | 1000 (billing currency) on the resource group | same |

## Decisions made at build time (details in ADR-064)

- **Kafka container, not Event Hubs.** `main` has 14 topics (`order-placed`, `payment-results`,
  `order-confirmed`, `menu-updated`, `refund-requested`, `payment-refunded`, `restaurant-response`,
  `delivery-assigned` + 6 `.dlq`). Event Hubs Standard caps at 10 per namespace. TCP ingress for Kafka is why
  the environment is VNet-integrated.
- **Cloud Kafka is not authenticated, unlike local.** The apps can log in to Kafka with SASL/SCRAM
  (`Kafka:SaslUsername`, set only in `appsettings.Development.json` for the local docker-compose broker). In
  Azure that setting is absent, so the apps connect without a login, and the broker is plain `PLAINTEXT`
  reachable only inside the private Container Apps environment. This is a known gap, not an oversight; see
  ADR-027's security addendum and ADR-064. Closing it needs a custom Kafka image, a CI step and secret
  plumbing, and cannot be verified until the pipeline has run once.
- **Redis Sentinel, not Azure Managed Redis, for `ha`.** `azurerm_managed_redis` exists, but Managed Redis
  has no user-triggered failover, so it can't be failed over on cue in class. Sentinel shows the election.
- **One Postgres server, four databases.** Database-per-service at the logical level; four servers would be
  four bills. Local compose keeps four containers.
- **Per-IP rate limits moved to the edge.** Behind Front Door and Envoy every request arrives from a proxy
  IP, so the gateway's in-memory limiter and the monolith's Redis limiter are raised out of the way
  (`100000`/min) and the WAF custom rule limits per client IP instead.
- **Small DB pools.** B1ms allows ~50 connections for the whole server; pools are sized so 5 Api replicas
  plus 3 services fit (see `postgres.tf`).
- **`Demo__EncryptionKey` is generated.** The monolith refuses to boot outside Development without it.

## Variables you might touch

| Variable | Default | Why |
|---|---|---|
| `mode` | `basic` | `ha` only on Day 14 |
| `image_tag` | `latest` | pin a git sha for a repeatable session |
| `max_replicas` | `5` | autoscale ceiling; keeps k6 inside the free grant and the DB pool math |
| `db_retry_enabled` | `false` | Day 14 flips it (or use `cloud-failover.ps1 -SetRetry on`) |
| `waf_rate_limit_per_minute` | `3000` | k6 from one laptop is one client IP; the Day 16 room sees it block first |
| `load_test_mode` | `false` | `cloud-up -LoadTest`: WAF threshold -> `waf_load_test_rate_limit_per_minute` (60000) |
| `kafka_consumer_scaling` | `false` | `cloud-up -KafkaScaling`: Payment KEDA Kafka-lag rule 1..4, topics auto-created with 3 partitions |
| `ghcr_username` / `ghcr_token` | empty | only if the GHCR packages are private |
| `budget_alert_emails` | required | a forgotten teardown must reach a human |

## Known rough edges (fix on the first dry run)

- `terraform validate` passes, but `plan` and `apply` have never run against real Azure.
- Front Door route/cache-rule interplay (caching enabled only by a rule override) needs a real check of the
  `x-cache` header.
- SSE uses the gateway URL by design (ADR-064 "Realtime split"), not Front Door.
- Redis Sentinel with hostname announce through Container Apps TCP ingress is the least-tested piece. If it
  misbehaves, run Day 14's Redis beat locally and keep the Postgres failover in the cloud.
- Origin lockdown: the gateway 403s requests without our `X-Azure-FDID` (Container Apps IP restrictions
  can't use the Front Door service tag, and the tag is shared by all Front Door customers anyway). Confirm
  the header value matches `resource_guid` on the first run. Premium + Private Link is the stronger option.
- Postgres private access: confirm the apps resolve the server FQDN to a private IP through the linked
  private DNS zone, and time the extra provisioning.
- KEDA `kafka` scaler (optional flag): confirm the scaler can reach `kafka:9092` (the environment's internal
  TCP name) from where KEDA runs.
