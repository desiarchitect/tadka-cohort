# Azure, from zero — your first real `cloud-up.ps1` run (ADR-064)

**Who this is for:** you, the instructor, doing this for the first time. `cloud-deploy.md` assumes an Azure
account, Terraform, and the Azure CLI already exist and are logged in — this doc is everything *before*
that point, plus your literal first run.

**Read this first, honestly:** per `docs/cost-model.md`'s "Real Azure bills" table and `cloud-deploy.md`'s
own status line, nobody has actually run this Terraform against real Azure yet — every number in those two
files says `TO BE FILLED after first dry run`. That first dry run is *this*. Expect the possibility of a
real, unhit-before bug (a Terraform apply error, a private-package pull failure) — this doc tells you how
to get to that point cleanly, not that the destination is guaranteed to work.

**Which branch:** `deploy/azure/` only exists from `day-12` onward (it's absent on `day-11` and earlier). If
you're not already on `day-12`, `day-13`–`day-16`, or `main`, `git checkout day-12` first.

---

## 1. Create the Azure account

1. Go to **[azure.microsoft.com/free](https://azure.microsoft.com/free)** and sign up. You need a phone
   number and a credit/debit card for identity verification — Azure will not charge it without you
   explicitly moving off the free offer, but the card is mandatory to create the account at all.
2. The free offer gives you a **30-day, $200 (~₹16,600) credit** plus 12 months of some always-free service
   quotas. Nothing this repo deploys (Postgres Flexible Server, Container Apps, Front Door) is in the
   always-free tier at the sizes used here, but the 30-day credit comfortably covers a `basic`-mode session
   — the planning estimate in `cost-model.md` is **~₹50-150 per ~4 hours**.
3. Once signed up, note your **subscription name** (shown in the portal's top bar or under "Subscriptions")
   — you'll need to select it explicitly in step 3 if you ever have more than one (a trial subscription and
   a pay-as-you-go one, for example).

## 2. Install the tools

```powershell
winget install Hashicorp.Terraform
winget install Microsoft.AzureCLI
```

Then confirm both actually landed on PATH and check the Terraform version — **the repo's own scripts only
check that `terraform` and `az` exist on PATH at all, not that Terraform is new enough**, so this is a step
you have to do yourself, not something that will fail loudly later if you skip it:

```powershell
terraform -version   # want >= 1.6
az version
```

If either command isn't found, close and reopen your terminal (winget's PATH update doesn't always apply
to an already-open shell).

## 3. Log in and pick your subscription

```powershell
az login
```

This opens a browser for you to sign in. Once done:

```powershell
az account show
```

If you have more than one subscription and the one shown isn't the one you want billed, set it explicitly —
nothing later in this pipeline will ask you again, it just silently uses whatever `az account show` returns
as "current":

```powershell
az account set -s "<your-subscription-name-or-id>"
```

If you skip `az login` entirely, every script (`cloud-up.ps1`, `cloud-down.ps1`, `cloud-failover.ps1`) fails
immediately and clearly with: `Not logged in to Azure. Run 'az login' (and 'az account set -s <subscription>') first.`

## 4. Get the 5 container images into GHCR — the step nothing automates

The deployment pulls 5 pre-built images: `ghcr.io/desiarchitect/tadka-cohort-{api,payment,delivery,restaurant,gateway}`.
Two ways to get access to them, pick one:

**Option A — the images already exist and are public (the normal case).** The `images` workflow in this repo
(`desiarchitect/tadka-cohort`) has built all 5 and they are public, so no credentials are needed to pull them.
`deploy/azure/variables.tf`'s default `image_prefix` already points at them. Try step 6 directly; come back here
only if Terraform fails to pull an image. You can check from any machine:
`docker pull ghcr.io/desiarchitect/tadka-cohort-api:latest` (no `docker login`).

**Option B — build and push your own.** Push to `main` (or, if you don't want to push code, trigger the
workflow manually: GitHub → the repo → Actions → "images" workflow → **Run workflow**, `workflow_dispatch` is
enabled). `.github/workflows/images.yml` runs `dotnet test` twice, then builds and pushes all 5 images,
tagged both `:<git-sha>` and `:latest`.

> **The gotcha, straight from the workflow file's own header comment:** the *first* push creates each of the
> 5 packages as **private**. Container Apps can't pull a private image without credentials. Fix it one of
> two ways:
> - **Make them public, once each:** GitHub → your profile/org → **Packages** → `tadka-<svc>` (repeat for
>   `api`, `payment`, `delivery`, `restaurant`, `gateway`) → **Package settings** → **Change visibility** → **Public**.
> - **Or authenticate Terraform to pull private images:** create a GitHub Personal Access Token with
>   `read:packages` scope, then create `deploy/azure/my.auto.tfvars` (already gitignored — never commit a
>   token) with:
>   ```hcl
>   ghcr_username = "<your-github-username>"
>   ghcr_token    = "<your-PAT>"
>   ```
>
> If you forked the repo or changed the code, also set `image_prefix = "ghcr.io/<your-github-username>/tadka-cohort"`
> in the same `my.auto.tfvars` file, so Terraform pulls *your* images, not the course's.

## 5. Set the budget alert email

```powershell
setx TADKA_ALERT_EMAIL you@example.com
```

`setx` persists it for future sessions (close and reopen your terminal for it to take effect this first
time). You can also just pass `-AlertEmail you@example.com` on every `cloud-up.ps1` call instead — either
works, `cloud-up.ps1` throws if neither is present. **The alert itself is not a safety net** — Cost
Management data lags 8-24 hours, so it emails you the day after a forgotten teardown, never during class.
Step 8's teardown is the real control.

**The value must be a real email address, with an `@`.** `cloud-up.ps1` now checks this first and stops in a
second. Before that check existed, a wrong value was accepted, the whole environment was built, and Azure
refused the budget alert only at the very end (`400: Notification cannot have invalid email addresses`). The
usual cause is a wrong value saved earlier with `setx`, for example the tenant domain
`yourname.onmicrosoft.com` instead of `you@example.com`. See what your terminal holds:
```powershell
$env:TADKA_ALERT_EMAIL                                          # what this window sees
[Environment]::GetEnvironmentVariable("TADKA_ALERT_EMAIL","User")   # what is saved for new windows
```
To fix a wrong value, run `setx TADKA_ALERT_EMAIL you@example.com` again, then **close and reopen the terminal**
(the old value stays in the window you already have open). Passing `-AlertEmail you@example.com` on the command
always wins over the saved value.

## 6. The first real run

```powershell
./scripts/cloud-up.ps1 -Mode basic -AlertEmail you@example.com -AutoDownAfterHours 4
```

- `-Mode basic` is the cheapest full system and what Day 12 and Day 16 use (`ha` is Day 14's failover demo
  only, and costs several times more per hour — don't use it for this first run).
- `-AutoDownAfterHours 4` registers a one-time Windows scheduled task that force-runs teardown 4 hours later
  if you forget — a backstop, not a plan. It only fires if your laptop is on and you're logged in.
- **On a Free Trial or Student subscription, add `-NoFrontDoor`.** Azure refuses Front Door there
  (`BadRequest: Free Trial and Student account is forbidden for Azure Frontdoor resources`). The session then
  runs without Front Door: the gateway URL is the public entry point, so there is no CDN cache hit, no WAF
  rate limit and no origin lock to show. Everything else works (4 services, Kafka, Postgres, Redis,
  autoscaling, the saga). Upgrade the subscription to pay-as-you-go to get the full Front Door demo.
- **Resource providers register themselves.** A fresh subscription has `Microsoft.App` (Container Apps) and
  `Microsoft.Cdn` unregistered, which fails the apply with `MissingSubscriptionRegistration`. `cloud-up.ps1`
  registers what it needs (free, one time, adds about a minute the first time).
- **Expected time: ~15-25 minutes**, mostly Postgres Flexible Server and Front Door provisioning — this is
  the *planning estimate* from `cloud-deploy.md`, not a measured number yet. Time your own run with a
  stopwatch; you're about to produce the first real number for that table (see step 8).
- The script ends by printing a Front Door URL (or the gateway URL with `-NoFrontDoor`) and running its own smoke test. You want to see `SMOKE OK`
  at the end. If a check fails, the script names the failing app — get its logs with:
  ```powershell
  az containerapp logs show -g rg-tadka-session -n <app-name> --follow
  ```

If it fails partway through `terraform apply` itself (not the smoke test) — genuinely possible, since this
exact path has never been run — read the Terraform error text directly; it's usually a quota, naming
collision, or permission issue on your specific subscription, not a bug in this repo's HCL. `cloud-down.ps1`
still works even after a partial/failed apply (Terraform tracks what it actually created).

## 7a. Check that everything works (one command)

```powershell
./scripts/cloud-check.ps1            # about 1 minute: Azure resources, security, one real order end to end
./scripts/cloud-check.ps1 -Burst     # also the autoscaling test (needs k6, about 4 more minutes)
```

It prints PASS, FAIL, WARN or SKIP per check and exits 1 if anything FAILED. It places one order and completes
its delivery so the rider goes back to the pool (only three riders are seeded; an order that is never
delivered keeps its rider busy and the next order waits). It works with or without Front Door.
## 7. Walk the results

```powershell
terraform -chdir=deploy/azure output                                                  # URLs, server names, app names
curl.exe -sI https://<front-door-host>/api/v1/restaurants | findstr /i x-cache        # TCP_MISS then TCP_HIT on a repeat
curl.exe -s -o NUL -w "%{http_code}`n" https://<gateway-host>/api/v1/restaurants       # 403 — the gateway URL is origin-locked to Front Door
```

Two URLs exist on purpose (ADR-064's "realtime split"): the **Front Door URL** for the API and cacheable
reads, and the **gateway URL** directly only for live tracking (SSE) — Front Door cuts long-lived
responses, so realtime traffic bypasses the CDN entirely. Everything else hitting the gateway URL directly
gets `403` unless it carries the `X-Azure-FDID` header Front Door itself would send.

## 8. Tear it down — always, every time

```powershell
./scripts/cloud-down.ps1
```

Wait for `Resource group rg-tadka-session is GONE`. If `terraform destroy` fails partway,
`./scripts/cloud-down.ps1 -Force` falls back to deleting the resource group directly. Either way, **open
the Azure portal yourself and confirm `rg-tadka-session` is actually gone** — don't trust the script alone
for something that bills by the hour.

**The next day:** open Cost Management, filter to this resource group, and record what it *really* cost —
this closes the loop the docs left open:
- `docs/cost-model.md` → "Real Azure bills per session" table → fill in the `basic` row (hours up, real bill).
- `docs/runbooks/cloud-deploy.md` → "Timings" table → fill in your measured `cloud-up`/`cloud-down` minutes
  (the script prints its own apply/healthy/total timings at the end of the run — copy them straight in).

## If this is your first time and something breaks

Things nothing in this pipeline currently checks for, so they show up as confusing downstream errors instead
of a clear message up front:
- **Terraform too old.** `Assert-Tool` only checks that `terraform` is *on PATH*, not that it's ≥1.6. If you
  hit a syntax error Terraform shouldn't produce, check `terraform -version` first.
- **A private GHCR package.** Nothing verifies image pull access before Terraform tries. A pull failure
  surfaces as a Container App stuck unhealthy, not a clear "package is private" message — see step 4.
- **Wrong subscription active.** `Assert-AzureLogin` just uses whatever `az account show` currently returns
  — it never asks you to confirm. Double-check `az account show` before your first `cloud-up.ps1` if you
  have more than one subscription.

For anything else, `cloud-deploy.md`'s own troubleshooting table and per-session checklist are the next
place to look — this doc only covers the one-time setup and your very first run.

## Appendix: every flag these scripts accept

**`scripts/cloud-up.ps1`**

| Flag | Type | Default | What it does |
|---|---|---|---|
| `-Mode` | `basic` \| `ha` | `basic` | `ha` = zone-redundant Postgres + read replica + Redis Sentinel (Day 14 failover only) |
| `-AlertEmail` | string | `$env:TADKA_ALERT_EMAIL` | Budget alert recipient — required, throws if empty and the env var isn't set |
| `-ImageTag` | string | `latest` | GHCR tag to deploy — a git sha or `latest` |
| `-DbRetry` | switch | off | Starts with `Database:EnableRetryOnFailure` on |
| `-SkipSmoke` | switch | off | Apply + wait for healthy only, skips the smoke test |
| `-WithManagedRedis` | switch | off | `ha`-mode only (throws otherwise) — also deploys Azure Managed Redis next to Sentinel, for comparison |
| `-LoadTest` | switch | off | Raises the Front Door WAF per-IP limit from 3000/min to 60000/min (Day 16) |
| `-KafkaScaling` | switch | off | Payment autoscales 1..4 on consumer lag (KEDA) |
| `-AutoDownAfterHours` | double, 0.5-24 | none | Registers the scheduled-task teardown backstop |

**`scripts/cloud-down.ps1`**

| Flag | Type | Default | What it does |
|---|---|---|---|
| `-Force` | switch | off | Falls back to `az group delete --yes --no-wait` if `terraform destroy` fails |

**`scripts/cloud-failover.ps1`** (Day 14 only — needs `-Mode ha` already up)

| Flag | Type | Default | What it does |
|---|---|---|---|
| `-Target` | `db` \| `redis`, required | none | Which dependency to fail over |
| `-Kind` | `forced` \| `planned` | `forced` | forced = brute kill; planned = graceful |
| `-SetRetry` | `on` \| `off` | none | Flips `Database__EnableRetryOnFailure` on all 4 app Container Apps first |
| `-DurationSeconds` | int | `240` | How long the request loop runs (failover triggers ~10s in) |
| `-RedisPrimary` | `redis-a` \| `redis-b` | `redis-a` | Which Redis node is currently primary |
| `-OutageSeconds` | int | `30` | How long the primary Redis node stays down (`redis forced` only) |

All three scripts require `terraform` and `az` on PATH and a live `az login` — see steps 2-3 above.
