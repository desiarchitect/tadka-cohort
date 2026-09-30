# ADR-064: Live cloud deployment: Azure Container Apps per session, AWS as reference

**Date:** 2026-09-23
**Status:** Proposed (Terraform written, not yet applied; becomes Accepted after the first dry run)
**Deciders:** Tadka Engineering Team (cohort instructor)
**Supersedes in part:** ADR-039 (the "black box, never deployed" stance; ADR-039's ECS/ALB target shape stays as the AWS reference)

## Context

Until now no day deployed Tadka to a real cloud. ADR-039 taught deploy as a black box: an AWS Terraform
skeleton that had never been applied (no ECS services, no internet gateway/NAT/routes, no ECR, no autoscaling,
no CloudFront, an RDS password of `"CHANGE_ME"`), plus a `deploy/README.md` that described things the code
did not have. The most "live" Day-12 moment was a screenshot of `terraform output`. Day 16's cost figures are
modeled, never a bill. Every URL in the course was `localhost`.

Constraints that shape the choice:

1. **Money.** One instructor account, a cohort-length budget. An environment that runs 24x7 for 8 weeks is
   out; one that exists for 3 sessions of ~4 hours each is in.
2. **"Results, not HCL."** Students must see the architecture working (CDN hit, one public entry, autoscale,
   failover, the bill). Nobody is taught Terraform syntax.
3. **Same code as local.** No cloud-only code paths. Everything is configuration.
4. **Facts found at build time on `main`:** 14 Kafka topics (8 + 6 DLQs); all four services call
   `Database.Migrate()` on startup; no `EnableRetryOnFailure` anywhere; 8 explicit transactions (3 Outbox
   relays with `FOR UPDATE SKIP LOCKED`, 4 Inbox consumers/handlers, 1 pessimistic coupon redeem);
   the gateway's rate limiter is in-memory per replica; `src/Tadka.Api/Dockerfile` did not build (it
   predates the Day-13 `Tadka.Telemetry` project reference) and the other four services had no Dockerfile.

## Decision

**Deploy live on Azure Container Apps, one resource group per class session, created by
`scripts/cloud-up.ps1 -Mode basic|ha` and destroyed by `scripts/cloud-down.ps1`.** Keep AWS as an honest,
plan-only reference in `terraform/`.

| Concern | Azure (live) | Why this one |
|---|---|---|
| CDN + WAF + TLS | Front Door Standard, short-TTL cache on `GET /api/v1/restaurants*`, WAF custom rate-limit rule (3000/min per IP; `-LoadTest` raises it for Day 16) | Managed cert on `*.azurefd.net`; the Day-6 cache lesson at a real edge |
| Realtime (SSE) | Live tracking goes to the gateway's own URL, not through Front Door | Front Door is built for cacheable request/response traffic; see "Realtime split" |
| Origin lockdown | Gateway 403s requests without our `X-Azure-FDID` (exempt: health probes, SSE path) | Standard-tier way to stop callers skipping the WAF/CDN; see "Origin lockdown" |
| Load balancer | Container Apps built-in Envoy ingress | No separate LB resource; say so, contrast with ALB |
| Gateway | Existing YARP `Tadka.Gateway`, the ONLY external ingress | Keeps Day 11's point: the gateway is an edge, not a trust boundary; services still validate the JWT |
| Services | 4 container apps, internal ingress, min 1 replica | Unreachable from the internet |
| Autoscaling | HTTP concurrency rule on gateway + api, 1..5. Optional: KEDA Kafka-lag rule on Payment, 1..4 | Visible under the existing k6 scripts on Day 16 |
| Postgres | Flexible Server, ONE server, 4 databases, **private access** (delegated subnet + private DNS zone, no public endpoint). `basic` B1ms; `ha` GP D2ds_v5 + zone-redundant HA + read replica | 4 servers = 4 bills; B1ms cannot fail over, so `ha` exists for one session only; the data tier is never public (Day 10) |
| Redis | `basic`: one container. `ha`: primary + replica + 3 Sentinels | See "Redis HA" below |
| Kafka | Single KRaft broker container app (TCP ingress, VNet environment) | 14 topics > Event Hubs Standard's 10 per namespace |
| Observability | OTEL Collector (contrib) exporting to Application Insights | The Day-13 seam: only the collector config changes |
| Secrets | `random_password` into Container App secrets | Key Vault named as the production upgrade |
| Images | GHCR, built by `.github/workflows/images.yml` after `dotnet test` (flag off AND on) | Outside the resource group, so images survive teardown |
| Cost guard | Budget alert (1000 in billing currency) on the resource group | A smoke alarm, not a brake |

**Redis HA.** The plan preferred Azure Managed Redis with HA, gated on it exposing a manual failover. The
provider has `azurerm_managed_redis`, but Microsoft's Managed Redis failover documentation lists failovers only
for patching, scaling and hardware faults, with no user-triggered failover. A demo that can't fail over on cue
doesn't work in class, so `ha` uses the documented fallback: Redis primary + replica + 3 Sentinels as container
apps, nodes announcing hostnames through the environment's TCP load balancer. (Azure Cache for Redis stops
new-cache creation from 1 Oct 2026, so it was not considered.)

**Managed Redis as an optional side-by-side.** `cloud-up.ps1 -Mode ha -WithManagedRedis` (Terraform
`enable_managed_redis = true`) also deploys Azure Managed Redis with `high_availability_enabled = true`
(`deploy/azure/managed-redis.tf`, default SKU `Balanced_B0`). The apps keep using Sentinel, so the live
failover still runs against Sentinel. The Managed Redis instance is there to be shown, not failed over: it is
the production answer to the same problem, where Azure runs the primary/replica election and nobody operates
Sentinels. The trade-off to name in class: you give up the ability to trigger or watch the failover, and in
return you get no Sentinel pages at 3am. It is off by default because it adds hourly cost to the one session
that uses it and proves nothing live. Confirm at the first dry run that `Balanced_B0` accepts HA; if not, raise
`managed_redis_sku`.

**Realtime split: SSE on the gateway URL, everything else through Front Door.** Front Door is a CDN: it is
built for request/response traffic that finishes quickly and can often be cached. A Server-Sent Events stream
is the opposite: one response that stays open for minutes. Front Door closes an origin response at the
profile's origin response timeout (`response_timeout_seconds` on `azurerm_cdn_frontdoor_profile`; we leave
the provider default), and it may buffer the stream on the way. So live order tracking
(`GET /api/v1/orders/{id}/events`) goes to the gateway's own hostname (`terraform output gateway_url`), the
"realtime" hostname, and cacheable reads plus the API go through Front Door. This is how real apps split
CDN traffic from realtime traffic (a separate `realtime.` or `ws.` hostname that skips the CDN). What it
costs: the realtime path has no WAF rate limit in front of it. The JWT still guards it (ADR-031), and the
gateway's own limiter is the only brake, which is why the gateway's limiter matters again (see "Per-IP rate
limiting" under Risks).

**Per-user stream cap (fix 5).** The gap above was real: one authenticated user could hold an unbounded
number of concurrent SSE streams against the gateway's public URL, since neither Front Door's WAF nor the
origin lock ever see that traffic. `OrderTrackingController` now caps concurrent streams per user (`sub`
claim) at 3, counted in-memory per replica via `SseStreamLimiter`, acquired before streaming starts and
released in a `finally` so a dropped client never leaks a slot; over the cap gets `429`, not a stream. This
is deliberately cheap (no Redis, no cross-replica coordination): a determined attacker with N Container
Apps replicas gets `3 x N` streams, not 3, an accepted trade-off at this scale, not a hard limit. Combined
with the resource-ownership check (fix 1, `order.CustomerId == User.UserId()` or Admin), this pair is the
realtime path's actual protection now that it skips Front Door: ownership stops a user from reading someone
else's order, and the cap stops one user from holding the gateway open indefinitely.

**Origin lockdown (Standard tier).** Without it, anyone who finds the `*.azurecontainerapps.io` URL can skip
the WAF and the CDN. Front Door adds an `X-Azure-FDID` header with the profile's id to every request it sends
to the origin. Terraform passes `azurerm_cdn_frontdoor_profile.fd.resource_guid` to the gateway as
`Gateway__RequiredFrontDoorId`, and `Tadka.Gateway` (`Security/FrontDoorOriginLock.cs`) returns 403 for any
request whose header does not match. Exempt: `/health` and `/health/ready` (Front Door and platform probes)
and the SSE path (the realtime split above). Empty or absent setting = off, so local compose, `dotnet run`
and the tests behave exactly as before; `tests/Tadka.Gateway.Tests` proves off, on, and the exemptions. Why
a header and not an IP allow-list: Front Door's egress IPs (the `AzureFrontDoor.Backend` service tag) are
shared by every Front Door customer, so an IP check alone would let any other customer's Front Door through;
the id is what makes it OURS. No dependency cycle: profile → gateway env var → origin `host_name`, and the
profile depends only on the resource group. The stronger option is Premium + Private Link to the origin
(the origin then has no public ingress at all): named, not built.

**Load-test mode.** The WAF rule allows 3000 requests per minute per client IP. k6 `stress.js` from one laptop
peaks around 150-300 requests per second (9,000-18,000 per minute), so through Front Door it gets mostly 403s
and the autoscaler never shows. `cloud-up.ps1 -LoadTest` sets `load_test_mode = true`, which raises the
threshold to `waf_load_test_rate_limit_per_minute` (60000/min). The rule stays on, just higher. The default
stays at 3000 on purpose: Day 16 first runs a 20-second burst, shows the 403s ("the edge protects you from
yourself; a real load test runs from many IPs or an allow-listed source"), then re-applies with `-LoadTest`.

**Optional Kafka consumer scaling (off by default).** `cloud-up.ps1 -KafkaScaling` sets
`kafka_consumer_scaling = true`: Payment gets a KEDA `kafka` custom scale rule (lag on `order-placed`, group
`tadka-payment`, lag threshold 5, max 4 replicas) and the broker gets `KAFKA_NUM_PARTITIONS=3`. Why the broker
default and not a `kafka-topics.sh --create --partitions 3 --if-not-exists` startup step: topics are
auto-created by the first producer, so a create step races the Outbox relay, and if the relay wins,
`--if-not-exists` silently leaves `order-placed` at 1 partition. A broker default has no race. Side effect:
every auto-created topic (DLQs too) gets 3 partitions; safe because every producer keys by order id, so
per-order ordering holds. `allowIdleConsumers = "true"` lets KEDA go past the partition count, so the room
sees 3 busy Payment replicas and a 4th with no partition: "partitions cap consumer parallelism" (Day 9), live.
Migration race: none in practice. Payment keeps min 1 replica, so the first replica migrates alone before
any lag exists; extra replicas start later against an up-to-date database, where `Database.Migrate()` only
reads `__EFMigrationsHistory`. Pool math on B1ms: Payment's pool drops to 2 when the flag is on, worst case
5 x (4 + 3) + 4 x 2 + 2 x 3 = 49 connections, still tight against B1ms's ~50: confirm on the dry run.

**App changes, all default OFF so local compose and the default test run are unchanged:**
- `Database:EnableRetryOnFailure` (plus `MaxRetryCount`, `MaxRetryDelaySeconds`) turns on Npgsql
  `EnableRetryOnFailure()` for every DbContext in every service.
- Every explicit transaction runs inside `db.Database.CreateExecutionStrategy().ExecuteAsync(...)`, with
  `ChangeTracker.Clear()` at the start of each attempt so a replay never reuses half-applied tracked state.
  With the flag off, the default strategy runs the lambda exactly once. Proven by
  `tests/Tadka.Api.Tests/Integration/ExecutionStrategyRetryTests.cs`.
- StackExchange.Redis `AbortOnConnectFail = false` set explicitly on all three multiplexers.
- No SASL/Event Hubs settings were added (Kafka is a container, see above). The local docker-compose broker does require SASL/SCRAM-SHA-256 (ADR-027 security addendum); this cloud broker does not. The apps only enable SASL when `Kafka:SaslUsername` is set, so the cloud configuration, which sets none, is unaffected. Securing the cloud broker would need a custom Kafka image carrying `docker/kafka-scram-entrypoint.sh`, a CI build/push step, and Terraform secret plumbing for the password; it is not done and not verified.
- `Gateway:RequiredFrontDoorId` (origin lockdown, above): empty = off.

## Consequences

### Positive
- The course finally has a public URL: a real CDN hit, one public entry, internal-only services, a real
  managed-Postgres failover, and a real bill next to the modeled one.
- The same images and code run locally and in the cloud; the cloud difference is only env vars.
- CI exists for the first time (tests, then 5 images).
- The Day-14 retry fix is a real, tested code change, not a slide.

### Negative
- One Postgres server for 4 databases: a noisy database affects the others. Local keeps 4 containers.
- Redis in a container: no persistence, restarts lose the cache (acceptable: cache, SSE, counters, geo).
- Kafka is one broker with ephemeral storage. A broker restart can lose un-consumed events; the Outbox
  tables let producers replay anything they had not published, but not what was published and lost.
- Front Door Standard has custom WAF rules only; the managed OWASP/DRS rule sets need Premium.
- The gateway's own `*.azurecontainerapps.io` URL is still reachable on the internet. It is locked to our
  Front Door by the `X-Azure-FDID` check (Standard tier, built), except the health probes and the SSE path.
  Premium + Private Link, where the origin has no public ingress at all, is the stronger option and is not
  built. (An earlier draft of this ADR said locking the origin *needs* Premium + Private Link. That was
  wrong: the header check is Microsoft's documented Standard-tier method.)
- Postgres private access makes provisioning a few minutes slower and means nobody, the instructor
  included, can `psql` into the cloud database from a laptop. Debug through the apps' logs.
- The Terraform was written without Terraform or an Azure login available; the first dry run is also its
  validation run.

### Risks and failure modes
- **Forgotten teardown = a bill.** `ha` mode costs roughly 5x `basic` per hour. Mitigations: one resource
  group, `cloud-down.ps1` confirms the group is gone, the budget alert emails a human, the runbook checklist.
- **Startup migration race.** All four services run `Database.Migrate()` (and the monolith runs `AuthSeeder`)
  on boot. Two replicas starting against a fresh database can race. Mitigation: min replicas 1 and
  scale-out only after the first revision is healthy; `AuthSeeder` is idempotent. Real fix when it matters:
  run migrations as a separate job (Container Apps job / ECS one-off task) before the rollout.
- **Per-IP rate limiting stops meaning "per client".** Behind Front Door + Envoy + YARP every request
  arrives from a proxy IP. The gateway's in-memory fixed-window limiter would then (a) treat all users as one
  client and (b) still be **per replica**, so with N replicas the effective limit is N x the configured one.
  The monolith's Redis limiter (ADR-049) is shared but has the same proxy-IP problem. Both are raised out of
  the way in the cloud and the per-client limit moves to the Front Door WAF rule.
  **The proper fix, written up but not built:** make the per-IP limiters see the real client IP again with
  ASP.NET Core's `ForwardedHeaders` middleware (`UseForwardedHeaders` with `XForwardedFor`), trusting only
  the proxies we own, and keep the WAF rule as the outer layer. Why it is not a one-line change:
  - The request passes through two proxies before our code: Front Door, then the Container Apps Envoy
    ingress. Each appends to `X-Forwarded-For`, so the header is a chain: `client, <Front Door egress IP>`
    and the socket peer is Envoy. `ForwardLimit` must be 2 and both hops must be in `KnownProxies` /
    `KnownNetworks`.
  - Envoy's address is an internal environment IP that can change per revision, and Front Door's egress is a
    set of ranges (the `AzureFrontDoor.Backend` service tag) shared by every Front Door customer, and it
    changes over time. The trust list has to be kept up to date, not hard-coded once.
  - Trusting the header blindly is spoofable: a client can send its own `X-Forwarded-For: 1.2.3.4`. Front
    Door appends rather than replaces, so a middleware that reads the left-most value, or trusts any proxy,
    gives every attacker a fresh rate-limit bucket per request. Only the entry added by the last trusted hop
    is real. And on the SSE path (gateway URL, no Front Door) the chain is one hop shorter, so the same
    middleware must handle both shapes.
  - The teaching line: "never trust X-Forwarded-For unless it came from your own proxy." Revisit when the
    realtime path (which has no WAF in front of it) needs a real per-client limit, or when the environment
    outlives a session.
- **DB failover without retry.** A forced Postgres failover drops every connection; with the flag off, writes
  fail with 500s for the whole failover window (to be captured on Day 14, not assumed).
- **Retry replays the whole unit.** With the flag on, a transient fault replays everything inside the
  strategy lambda. That is safe for the Outbox relays (at-least-once, the Inbox dedups) and the Inbox
  consumers (the transaction rolled back), but `OrderPlacedConsumer` calls the payment gateway *inside* the
  transaction: the Pending row rolls back, so a replay calls the gateway again. Harmless with the fake gateway;
  a real PSP needs an idempotency key (the order id) on the charge call.
- **Unwrapped transactions throw under the flag.** Any future `BeginTransaction` outside
  `CreateExecutionStrategy().ExecuteAsync` fails with "does not support user-initiated transactions" when the
  flag is on. `ExecutionStrategyRetryTests` pins this behavior; CI runs the whole suite with the flag on.
- **Redis Sentinel without persistence.** If the primary restarts faster than `down-after-milliseconds`
  (5 s), it comes back empty *as primary* and the replica resyncs from it: the cache is wiped rather than
  failed over. The failover script therefore deactivates the primary for 30 s instead of restarting it.
  Classic Redis guidance: don't auto-restart a persistence-less primary.
- **Day-10 RS256 keys break under autoscaling if ever brought to `main`.** `main` uses HS256 with a shared key
  from a secret, so every replica can verify every token. The `day-10` branch (not on `main`) signs with RS256
  keys generated **in memory** per process and serves them over JWKS. Under autoscaling, a token signed by Api
  replica A cannot be verified with the JWKS served by replica B (different key), so logins fail at random.
  The key must be persisted (a Container App secret or Key Vault) before that code reaches `main` and runs
  with more than one replica.

- **`ha` takes long to provision.** Zone-redundant HA standby plus a read replica can take 30+ minutes
  (estimate; measure it). Start `cloud-up -Mode ha` at **T-75**, not T-45, and run a full dry run of every
  cloud day the day before class.
- **The budget alert is late.** Cost Management data lags roughly 8-24 hours, so the budget email catches a
  forgotten teardown the next day, never during class. The real controls are `cloud-down.ps1` and the
  optional backstop `cloud-up.ps1 -AutoDownAfterHours <n>`, a one-time Windows scheduled task (current
  user) that runs `cloud-down.ps1 -Force` unattended. It needs the laptop on and the user logged in.

### Cost
Estimates from planning, **not a bill**: `basic` ~Rs 50-150 per ~4-hour session (B1ms covered by the 12-month
free hours on a free account, Front Door base fee prorated, Container Apps mostly inside the monthly free
grant); `ha` roughly $0.6-0.8/hour, ~Rs 250-350 for a 4-hour Day-14 session. The real Cost Management figure
for each mode goes into `docs/cost-model.md` after the first dry run. The AWS reference is modeled at
~Rs 31,300/month for the built shape (`docs/cost-model.md`) and is never applied.

**Container Apps free grant, from the real container sizes** (arithmetic; the bill is the check). The
monthly grant per subscription is 180,000 vCPU-seconds and 360,000 GiB-seconds. At minimum replicas:

| App (`deploy/azure`) | Replicas | vCPU | GiB |
|---|---|---|---|
| api, payment, delivery, restaurant (`apps.tf`) | 4 x 1 | 4 x 0.5 = 2.0 | 4 x 1 = 4.0 |
| gateway (`apps.tf`) | 1 | 0.5 | 1.0 |
| kafka (`kafka.tf`) | 1 | 1.0 | 2.0 |
| redis, `basic` (`redis.tf`) | 1 | 0.25 | 0.5 |
| otel-collector (`otel.tf`) | 1 | 0.5 | 1.0 |
| **`basic` total** | | **4.25** | **8.5** |

- One `basic` session of 4 hours = 14,400 s: 4.25 x 14,400 = **61,200 vCPU-s** and 8.5 x 14,400 =
  **122,400 GiB-s**. That is 34% of each grant, so about **2.9 `basic` sessions** fit in a month
  (180,000 / 61,200 = 2.94; 360,000 / 122,400 = 2.94).
- `ha` swaps the one Redis for 2 nodes + 3 Sentinels (5 x 0.25 vCPU, 5 x 0.5 GiB): 5.25 vCPU and 10.5 GiB,
  so 4 hours = 75,600 vCPU-s and 151,200 GiB-s.
- Days 12 + 16 (`basic`) + Day 14 (`ha`) in one calendar month = 61,200 x 2 + 75,600 = **198,000 vCPU-s**
  (18,000 over) and 122,400 x 2 + 151,200 = **396,000 GiB-s** (36,000 over). Small, and billed at the
  Container Apps per-second rate.
- What pushes it further over: Day 16 autoscale (each extra gateway/api replica adds 0.5 vCPU and 1 GiB while
  it runs), optional Payment Kafka scaling (up to 3 more x 0.5 vCPU), the T-75 start for `ha`, and the
  day-before dry runs, which are sessions too. With dry runs, expect the grant to cover roughly the first
  1.5 class days of the month and the rest to bill. Idle-rate pricing for replicas that are not processing
  requests lowers the real number; the Cost Management figure is the truth, recorded in `docs/cost-model.md`.
- The grant covers Container Apps only. Front Door, Postgres, Log Analytics and Application Insights bill
  separately.

## Alternatives Considered

### Option A: AWS ECS Fargate (make `terraform/` real and apply it)
- Pros: matches ADR-039 and most Indian job descriptions; longer free credit (up to $200 over 12 months).
- Cons: ALB, NAT, Fargate and RDS bill every hour they exist; MSK alone costs more than the rest combined;
  more IAM/VPC surface to get right before anything answers.
- Why rejected for the live path: per-session cost and setup time. Kept as the plan-only reference.

### Option B: Azure Container Apps (chosen)
- Pros: built-in ingress/LB and HTTP autoscaling at no extra cost; scale-to-zero plus a monthly free grant
  (180k vCPU-seconds, 360k GiB-seconds, 2M requests); the $200 trial credit covers the cohort.
- Cons: less common in Indian JDs than AWS; TCP ingress needs a VNet-integrated environment.

### Option C: AKS (Kubernetes)
- Pros: portable, the industry default at large scale.
- Cons: control plane, ingress controller, node pools, Helm: many concepts for the same teaching result;
  nodes bill while idle.
- Why rejected: same reason ADR-039 rejected EKS at this scale.

### Option D: One VM + docker compose
- Pros: identical to local, cheapest single box.
- Cons: no managed failover, no autoscaling, no CDN/WAF story, one box = one failure domain. It would teach
  nothing Day 12/14/16 can't already show on a laptop.
- Why rejected: the point is to see managed LB, autoscale and failover working.

## Teaching fields

- **Topic:** where and how the 4-services-plus-gateway system runs for real, per session.
- **Options:** AWS ECS Fargate · Azure Container Apps · AKS/EKS · VM + compose.
- **Choice:** Azure Container Apps, one resource group per session (`basic` Day 12 + 16, `ha` Day 14); AWS
  Terraform kept as a plan-only reference.
- **Why:** cheapest way to show a CDN, one public entry, autoscaling and managed failover live, with the same
  images and code as local.
- **Trade-off:** one Postgres server (not four), Redis and Kafka as containers, Standard-tier WAF, an
  instructor-only Terraform that students never edit.
- **Failure mode:** teardown forgotten → a bill (the budget alert lags 8-24 h); migrations race on
  scale-out; per-replica/proxy-IP rate limits; the WAF blocks a one-laptop load test; SSE cut at the CDN; a DB
  failover throws 500s until retry is on; RS256 in-memory keys (day-10) break under autoscale.
- **Revisit when:** the environment must outlive a session (then: Private Link to the origin, Key Vault,
  managed Kafka, a migration job, `ForwardedHeaders` with a maintained trust list); the cohort moves to AWS
  credits; Managed Redis gains a user-triggered failover; Tadka's topics drop to 10 or fewer (Event Hubs
  Standard becomes possible).
- **Cross-stack equivalents:** the deploy is language-neutral: any container (Spring Boot jar, Node, Go
  binary) runs the same way. The retry/execution-strategy pattern maps to Spring `@Retryable` around a
  `@Transactional` unit (retry the whole transaction, never a statement inside it), Node `p-retry` around a
  Knex/Prisma `$transaction` callback, Go a retry loop around a `sql.Tx` closure. StackExchange.Redis
  `AbortOnConnectFail=false` ≈ Lettuce auto-reconnect, ioredis `retryStrategy`, go-redis `MaxRetries`.

## References
- Plan: `deploy/azure/` (Terraform), `deploy/azure/README.md`, `docs/runbooks/cloud-deploy.md`
- Scripts: `scripts/cloud-up.ps1`, `scripts/cloud-down.ps1`, `scripts/cloud-failover.ps1`
- CI: `.github/workflows/images.yml`
- ADR-015 (pool sizing), ADR-016 (read replica), ADR-018/020/044 (Redis roles, degradation), ADR-026
  (database-per-service), ADR-027/028 (Kafka, Outbox), ADR-031 (per-service JWT), ADR-035 (gateway),
  ADR-039 (superseded in part), ADR-040 (OTEL), ADR-049 (distributed rate limiting)
- Microsoft Learn: "Failover and patching - Azure Managed Redis"; Azure Front Door "secure traffic to
  origins" (the `X-Azure-FDID` check); Azure Database for PostgreSQL flexible server "networking with private
  access (VNet integration)"; Azure Container Apps billing (free grant)
- Code: `src/Tadka.Gateway/Security/FrontDoorOriginLock.cs`, `tests/Tadka.Gateway.Tests`
