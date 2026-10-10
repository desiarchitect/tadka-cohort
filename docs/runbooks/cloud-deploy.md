# Runbook: live cloud deploy per session (Azure, ADR-064)

Instructor-only. Students never run this; they use the URL it prints. Three sessions use it:

| Day | Mode | What the room sees |
|---|---|---|
| Day 12 (Restaurant extraction + deploy) | `basic` | Public Front Door URL, a CDN hit on the menu, only the gateway public, count the hops |
| Day 14 (Resilience & chaos) | `ha` | Postgres forced failover before/after the retry fix, replica lag, Redis Sentinel failover |
| Day 16 (Load test + cost) | `basic`, then `-LoadTest` | the WAF blocks our own k6 burst, then k6 against the Front Door URL, gateway/api replicas scale 1 → 5 → 1, CDN on vs off, Rs per 1,000 orders, the real bill |

Students who want their own copy: [`self-deploy.md`](self-deploy.md) (optional, their own free account).

Never done any of this before — no Azure account, no Terraform/CLI installed, never run `cloud-up.ps1`?
Start at [`azure-getting-started.md`](azure-getting-started.md) instead; it covers everything before the
"One-time prerequisites" section below.

> **Status:** applied once (2026-10-04) on an Azure **Free Trial** subscription with `-Mode basic -NoFrontDoor`,
> and `cloud-up` ended with SMOKE OK. Front Door, `ha` mode and Redis Sentinel have not been applied yet, and
> the timings and the real bill below are still to be filled. Record them; don't reuse the estimates as facts.
>
> **Free Trial or Student subscription? Use `-NoFrontDoor`.** Azure refuses Front Door there (`BadRequest: Free
> Trial and Student account is forbidden for Azure Frontdoor resources`). `./scripts/cloud-up.ps1 -Mode basic
> -NoFrontDoor` skips Front Door: the gateway URL becomes the public entry point. You lose the CDN cache hit,
> the WAF rate limit and the origin-lock demo (and the Day 16 "WAF blocks our own k6 burst" beat). The 4
> services, Kafka, Postgres, Redis, autoscaling and the saga all work. Upgrade to pay-as-you-go for the full
> Front Door demo. Why it is a switch and not the default: ADR-064, addendum "Front Door is optional".

## One-time prerequisites

1. **Azure account** with a subscription the instructor owns. Log in once per machine:
   `az login` then `az account set -s <subscription-id>`. Nobody else needs Azure access.
2. **Install the tools** (not needed by students):
   - Terraform >= 1.6: `winget install Hashicorp.Terraform`
   - Azure CLI: `winget install Microsoft.AzureCLI`
   - k6 (Day 16 only; already part of the cohort setup)
3. **Images in GHCR.** Push to `main` (or run the `images` workflow by hand). It runs `dotnet test` twice
   (retry flag off and on), then pushes `ghcr.io/desiarchitect/tadka-{api,payment,delivery,restaurant,gateway}`
   tagged `:<sha>` and `:latest`.
   - First push creates the packages as **private**. Either make each package public once (GitHub →
     Packages → `tadka-<svc>` → Package settings → Change visibility → Public), or put a PAT with
     `read:packages` in `ghcr_username`/`ghcr_token` (a gitignored `deploy/azure/*.auto.tfvars` file).
4. **Budget email.** `setx TADKA_ALERT_EMAIL you@example.com` (or pass `-AlertEmail` every time), then reopen the
   terminal. It must be a real address with an `@`; `cloud-up` rejects anything else before it builds anything.
   Check it with `$env:TADKA_ALERT_EMAIL`.

## Per-session checklist

**The day before (every cloud day)**
- [ ] Full dry run: the exact `cloud-up` command for tomorrow, the day's beat end to end, then `cloud-down`.
      Record the real timings in the table below. A dry run is a billed session too (see "Free grant").

**T-75 min for `ha` (Day 14) / T-45 min for `basic` (Day 12, Day 16)**
- [ ] `az account show` shows the right subscription.
- [ ] The `images` workflow on the latest `main` commit is green. Optionally pin it: `-ImageTag <sha>`.
- [ ] `./scripts/cloud-up.ps1 -Mode basic -AutoDownAfterHours 7` (Day 12/16; add `-NoFrontDoor` on a Free Trial or Student subscription) or
      `-Mode ha -AutoDownAfterHours 8` (Day 14). Leave it running. `ha` (HA standby + replica) can take
      30+ minutes to provision, which is why it starts at T-75.
      `-AutoDownAfterHours` is the optional backstop: a one-time Windows scheduled task (current user) that
      runs `cloud-down.ps1 -Force` at that time, in case you forget. Pick a time well after class ends.
      Day 14 optional: add `-WithManagedRedis` to also deploy Azure Managed Redis (HA on) next to Sentinel,
      for a side-by-side comparison. The apps keep using Sentinel. Adds hourly cost to that session only.

**T-30 min (once `cloud-up` has finished)**
- [ ] Run the end-to-end check from a **new** PowerShell window, in the **same clone** you ran `cloud-up` from (it reads
      the gateway address from that clone's Terraform state): `./scripts/cloud-check.ps1`. About a minute. It places one
      real order, checks security and the saga, and delivers the order so the rider is freed. Every line should say
      PASS (Front Door lines say SKIP on a `-NoFrontDoor` session). Add `-Burst` for the autoscaling test (needs `k6`).
      From another clone: `./scripts/cloud-check.ps1 -GatewayUrl https://<gateway-host>`.
      Only three riders are seeded and every undelivered order keeps one busy (the `cloud-up` smoke test leaves one each
      time). If they are all taken the check shows a **WARN**, not a FAIL; run `./scripts/cloud-check.ps1 -FreeRiders`
      and check again.

**T-15 min**
- [ ] The script ended with `SMOKE OK`. If a check failed, it names the app: `az containerapp logs show -g rg-tadka-session -n <app> --follow`.
- [ ] Open the Front Door URL in a browser tab and the resource group in the portal (for the "map each box" walk).
- [ ] Note the time. The bill starts now.

**In class:** see the day's README / break-kit for the beat. Useful commands:
```powershell
terraform -chdir=deploy/azure output                      # URLs, server names, app names
az containerapp replica list -g rg-tadka-session -n api -o table   # watch autoscaling (Day 16)
curl.exe -sI https://<front-door-host>/api/v1/restaurants | findstr /i x-cache   # TCP_MISS then TCP_HIT
curl.exe -s -o NUL -w "%{http_code}\n" https://<gateway-host>/api/v1/restaurants  # 403: origin locked to Front Door
curl.exe -N -H "Authorization: Bearer <jwt>" https://<gateway-host>/api/v1/orders/<id>/events  # SSE: realtime uses the gateway URL
```

Two URLs on purpose (ADR-064 "Realtime split"): **Front Door** for the API and cacheable reads, the
**gateway URL** only for live tracking (SSE), because Front Door cuts long-lived responses. Everything
else on the gateway URL returns 403 unless it carries our `X-Azure-FDID` (`terraform output front_door_id`).
Postgres has **no public endpoint** (private access): you cannot `psql` into it from the laptop.

**Right after class (do not skip)**
- [ ] `./scripts/cloud-down.ps1`. Wait for "Resource group ... is GONE". On success it also removes the
      `-AutoDownAfterHours` scheduled task.
- [ ] Next day: Cost Management → resource group filter → record the session's real spend in
      `docs/cost-model.md` ("Real Azure bill" rows).

## Timings

| Step | Expected (planning estimate) | Measured on first dry run |
|---|---|---|
| `cloud-up -Mode basic` (apply + healthy + smoke) | ~15-25 min, mostly Postgres and Front Door | TO BE FILLED |
| `cloud-up -Mode ha` | 30+ min possible: HA standby + read replica (hence T-75) | TO BE FILLED |
| Extra time from Postgres private access (delegated subnet + private DNS) | a few minutes | TO BE FILLED |
| Re-apply with `-LoadTest` (WAF policy change only) | unknown | TO BE FILLED |
| Front Door route propagation after apply | several minutes (404 until then) | TO BE FILLED |
| `cloud-down` | ~10-20 min | TO BE FILLED |

`cloud-up.ps1` prints its own apply/healthy/total minutes. Copy them into the table.

## Cost per mode (estimates, not a bill)

| Mode | Estimate | Main drivers |
|---|---|---|
| `basic` | ~Rs 50-150 per ~4 h session | Front Door base fee (prorated), Postgres B1ms (free hours on a free account for 12 months), Container Apps (mostly inside the monthly free grant) |
| `ha` | ~$0.6-0.8/h, ~Rs 250-350 per 4 h | GP D2ds_v5 x2 (primary + HA standby) + a replica, plus the `basic` items |
| `ha` + `-WithManagedRedis` | `ha` plus the Managed Redis HA hourly price (not measured yet) | Azure Managed Redis `Balanced_B0` with HA. Record the real figure after the first run that uses it. |

Real numbers go in `docs/cost-model.md` after each dry run. A budget alert (1000 in the billing currency) is
created with every environment. It only emails; `cloud-down` is the real control.

**The budget alert is 8-24 hours late.** Cost Management data lags, so the alert catches a forgotten
teardown the NEXT day, never during class. That is why `cloud-down` is on the checklist and why
`cloud-up -AutoDownAfterHours <n>` exists as a backstop. The backstop runs only if the laptop is on and you
are logged in (it uses your `az login`); its output goes to `%TEMP%\tadka-auto-down.log`.

### Free grant (Container Apps), from the real container sizes

Monthly grant per subscription: 180,000 vCPU-seconds and 360,000 GiB-seconds. `basic` at minimum replicas
is 4.25 vCPU and 8.5 GiB (4 services x 0.5/1 GiB, gateway 0.5/1, Kafka 1/2, Redis 0.25/0.5, OTEL 0.5/1).

| | vCPU-s | GiB-s | Share of grant |
|---|---|---|---|
| one `basic` session, 4 h (14,400 s) | 4.25 x 14,400 = 61,200 | 8.5 x 14,400 = 122,400 | 34% / 34% |
| one `ha` session, 4 h (5.25 vCPU, 10.5 GiB: 2 Redis + 3 Sentinels) | 75,600 | 151,200 | 42% / 42% |
| Days 12 + 14 + 16 in one month | 198,000 | 396,000 | 110% / 110% |

About 2.9 `basic` sessions fit. Autoscale replicas on Day 16, the T-75 `ha` start and the day-before dry
runs all add to it, so the later sessions of the month bill. Full working in ADR-064 "Cost". Check it
against the real bill.

## Day 14: the failover beat

Needs `cloud-up -Mode ha`. Every number is printed by the script and saved to a CSV; copy it into
`break-kit-day-14.md`. Don't round, don't reuse a previous run's numbers.

```powershell
# 1. Failure first: retry OFF (the default), forced failover. Expect 5xx for the failover window.
./scripts/cloud-failover.ps1 -Target db -Kind forced

# 2. The fix: flip Database__EnableRetryOnFailure on the 4 services (new revisions), run it again.
./scripts/cloud-failover.ps1 -Target db -Kind forced -SetRetry on      # or -Kind planned

# 3. Replica lag (Day 5 callback): place an order, read it via the replica, then show the metric
az monitor metrics list --resource <replica-resource-id> --metric physical_replication_delay_in_seconds --interval PT1M -o table

# 4. Redis: primary down for 30 s -> Sentinels promote the replica -> clients follow
./scripts/cloud-failover.ps1 -Target redis -Kind forced                 # next run: -RedisPrimary redis-b

# Optional talking point: promote the read replica (regional DR). Async = the last seconds of writes can be lost.
# az postgres flexible-server replica promote -g rg-tadka-session -n <replica-name>
```

Notes:
- The loop hits the **gateway URL**, not Front Door, so the 30 s menu cache can't hide the blip. The gateway
  URL is locked to Front Door, so the script sends `X-Azure-FDID` itself and counts any 403 as an error.
- `-SetRetry` changes env vars outside Terraform. A later `terraform apply` resets them unless you use
  `cloud-up.ps1 -DbRetry`.
- `-Target redis -Kind planned` uses `az containerapp exec` (interactive). If it can't attach from the
  script, run `az containerapp exec -g rg-tadka-session -n sentinel-1 --command "redis-cli -p 26379 SENTINEL FAILOVER tadka"` by hand.

## Day 16: autoscale + the bill

```powershell
$fd = terraform -chdir=deploy/azure output -raw front_door_url

# 1. Failure first: the environment came up WITHOUT -LoadTest (WAF 3000/min per IP). A 20 s burst from
#    one laptop gets 403s from the edge. "The edge protects you from yourself."
#    300 VUs x ~1 request/s each (2 reads per 1-3 s think time) = ~6,000 requests in 20 s, above 3000/min.
#    CLI --vus/--duration override smoke.js's own 1 VU / 30 s. Expect browse_errors to jump: those are 403s.
k6 run -e BASE_URL=$fd --vus 300 --duration 20s k6/smoke.js

# 2. The fix: re-apply with the SAME switches plus -LoadTest (WAF threshold -> 60000/min; the rule stays on).
./scripts/cloud-up.ps1 -Mode basic -LoadTest -SkipSmoke

# 3. The real run. Orders need a token + customer: -e ORDER_TOKEN=<jwt> -e ORDER_CUSTOMER_ID=<id> (k6/lib.js)
k6 run -e BASE_URL=$fd k6/average-load.js
k6 run -e BASE_URL=$fd k6/stress.js     # watch: az containerapp replica list -g rg-tadka-session -n api -o table
```
- One laptop = one client IP. `stress.js` peaks around 150-300 requests/s (9,000-18,000/min), far above
  3000/min. A real load test runs from many IPs or from an allow-listed source.
- Front Door counts the rate limit per POP and over a window, so the first 403 may lag the burst start by
  some seconds. Capture when it appears on the dry run.
- **CDN on vs off** (after `-LoadTest`): the same short browse run, twice.
  ```powershell
  $gw   = terraform -chdir=deploy/azure output -raw gateway_url
  $fdid = terraform -chdir=deploy/azure output -raw front_door_id
  k6 run -e BASE_URL=$fd                  -e PEAK_VUS=100 k6/average-load.js   # CDN on
  k6 run -e BASE_URL=$gw -e FDID=$fdid    -e PEAK_VUS=100 k6/average-load.js   # CDN off (gateway direct)
  ```
  The gateway URL is locked, so the CDN-off run passes `FDID` and k6 sends the header Front Door would.
  Compare the api/restaurant replica counts (`az containerapp replica list`) and `cdn_hit` in the k6
  summary (share of browse reads with an `X-Cache: ...HIT` header; only the Front Door run has it).
- **Rs per 1,000 orders:** `orders_placed` in the k6 end-of-test summary (k6/lib.js counts every 201; needs
  `ORDER_TOKEN` + `ORDER_CUSTOMER_ID`) and the session's real cost from Cost Management the next day:
  cost / orders_placed x 1000. Postgres is private, so a laptop SQL count is not possible; the k6 counter is
  the source.
- `max_replicas` (default 5) also bounds the Postgres connection math on B1ms (see `deploy/azure/postgres.tf`).
- Optional, Could-tier: `cloud-up ... -KafkaScaling` gives Payment a KEDA Kafka-lag rule (1..4 replicas) and
  `order-placed` 3 partitions. Under k6 with orders on, watch
  `az containerapp replica list -g rg-tadka-session -n payment -o table`: replicas can reach 4 but only 3
  get a partition. Must be set at first apply (partitions are fixed when the topic is auto-created).

## When things go wrong

| Symptom | Likely cause | Fix |
|---|---|---|
| `BadRequest: Free Trial and Student account is forbidden for Azure Frontdoor resources` | Azure does not allow Front Door on a Free Trial or Student subscription | re-run with `cloud-up.ps1 -NoFrontDoor` (no CDN, WAF or origin lock), or upgrade to pay-as-you-go |
| `MissingSubscriptionRegistration ... namespace 'Microsoft.App'` | resource provider not registered on a new subscription | `cloud-up` now registers them; by hand: `az provider register --namespace Microsoft.App --wait` (also `Microsoft.Cdn`) |
| Services log `Name or service not known (otel-collector)` | the short name did not resolve from the services | fixed: `OTEL_EXPORTER_OTLP_ENDPOINT` uses the collector's full internal address (`apps.tf`) |
| `a resource with the ID ... rg-tadka-session already exists` | a group from an earlier run (often another clone, whose state Terraform cannot see) | delete it (`az group delete -n rg-tadka-session --yes`), then run `cloud-up` again; keep to one clone per environment |
| Front Door returns 404 right after apply | route still propagating | wait; `cloud-up` polls up to 25 min |
| An app restarts in a loop | migration/seed failure, or the DB host does not resolve to a private IP | `az containerapp logs show -g rg-tadka-session -n <app>`; check the private DNS zone `*.private.postgres.database.azure.com` has an A record and a link to `vnet-tadka` |
| Gateway URL returns 403 | origin lockdown: no/wrong `X-Azure-FDID` (expected for anything but `/health`, `/health/ready` and SSE) | use the Front Door URL; scripts send `terraform output front_door_id` |
| k6 through Front Door gets mostly 403 | WAF per-IP limit (3000/min) | expected before `-LoadTest`; re-run `cloud-up` with `-LoadTest` |
| SSE stream stops after a while via Front Door | CDN origin timeout on a long-lived response | use the gateway URL for SSE (by design) |
| `ImagePullBackOff`-style errors | GHCR packages private | make them public or set `ghcr_token` |
| Api 500s with "too many connections" | replicas x pool > B1ms limit | lower `max_replicas` or pool sizes |
| `cloud-up` stops partway with `terraform apply failed` | one resource was refused after others were built; the error text is printed above the failure | fix the cause named in the error, then **run the same `cloud-up` command again**: Terraform keeps what it built and carries on from there. Do not delete the group first. If you are abandoning the session, run `./scripts/cloud-down.ps1`. |
| `400: Notification cannot have invalid email addresses` (budget) | `TADKA_ALERT_EMAIL` (or `-AlertEmail`) is not an email address, for example `name.onmicrosoft.com` | `cloud-up` now checks this before building. Fix it with `setx TADKA_ALERT_EMAIL you@example.com`, reopen the terminal, and run `cloud-up` again (or pass `-AlertEmail you@example.com`) |
| `cloud-check.ps1` shows `WARN  a rider is free for a new order` and skips the rider checks | all 3 seeded riders are held by earlier undelivered orders (smoke tests, demo orders). The deployment is fine. | `./scripts/cloud-check.ps1 -FreeRiders`, then check again |
| `cloud-check.ps1` fails straight away with a Terraform error about outputs or state | it was run from a different clone than `cloud-up` (the gateway address comes from that clone's Terraform state), or `cloud-up` is still running | run it from the same clone after `cloud-up` has finished, or pass `-GatewayUrl https://<gateway-host>` |
| `terraform destroy` fails | lost/partial state | `./scripts/cloud-down.ps1 -Force` (deletes the resource group with az) |
