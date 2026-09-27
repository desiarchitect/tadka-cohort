# Deploy Tadka on your own free Azure account (optional)

**Optional. Not class work, not graded.** In class you only use the URL the instructor prints ("results, not
HCL"). This page is for students who want a live URL of their own for a portfolio or resume. It uses the same
scripts the instructor uses (ADR-064).

> **Cost warning. Read this first.**
> - This creates real, billed Azure resources: Front Door, a Postgres server, a VNet, container apps, Log
>   Analytics. Some of them bill every hour they exist, whether anyone uses them or not.
> - A free account's credit and free grants cover a few short sessions. They do **not** cover leaving it
>   running for days. See the free-grant arithmetic in `docs/runbooks/cloud-deploy.md`.
> - The budget alert emails you 8-24 hours late. It never stops anything.
> - **Always run `cloud-down` when you are done.** Every time. Then check the portal that the resource group
>   `rg-tadka-session` is gone.
> - You are responsible for your own bill. If you are not comfortable with that, don't do this; the local
>   `docker compose` setup teaches the same architecture for free.

## What you need

Never created an Azure account or installed any of this before? [`azure-getting-started.md`](azure-getting-started.md)
walks the account-creation and first-run steps in more detail than this page does; come back here once
you're logged in.

1. Your own Azure account (the free account is enough to start). Log in once: `az login`, then
   `az account set -s <your-subscription-id>`.
2. Terraform >= 1.6 (`winget install Hashicorp.Terraform`) and the Azure CLI (`winget install Microsoft.AzureCLI`).
3. Container images. The default in `deploy/azure/variables.tf` is the course's
   `ghcr.io/desiarchitect/tadka-*`; that only works once those packages are public (ask the instructor).
   Otherwise, or if you changed the code, push your fork to GitHub so `.github/workflows/images.yml` builds
   your own images, make the packages public (or set `ghcr_username`/`ghcr_token`), and set `image_prefix`
   to `ghcr.io/<your-github-user>/tadka` in a gitignored `deploy/azure/my.auto.tfvars` file.
4. An email address for the budget alert.

## Bring it up

```powershell
./scripts/cloud-up.ps1 -Mode basic -AlertEmail you@example.com -AutoDownAfterHours 4
```

- `basic` only. `ha` costs several times more per hour and exists for the Day 14 failover demo.
- `-AutoDownAfterHours 4` registers a Windows scheduled task that runs `cloud-down` 4 hours later, as a safety
  net. It only runs if your laptop is on and you are logged in. It is a backstop, not a plan.
- It takes a while (Postgres and Front Door are the slow parts). The script prints the Front Door URL at
  the end, runs a smoke test, and prints how long each step took.

Your portfolio URL is the **Front Door URL** (`https://tadka-xxxxxx....azurefd.net`). Take screenshots of
the smoke test, the `x-cache: TCP_HIT` header and the Application Insights map while it is up: the URL only
lives as long as the session.

## Take it down (always)

```powershell
./scripts/cloud-down.ps1
```

Wait for `Resource group rg-tadka-session is GONE`. If `terraform destroy` fails, run
`./scripts/cloud-down.ps1 -Force` (deletes the resource group directly). Then open the Azure portal and
confirm there is no `rg-tadka-session` left. The next day, check Cost Management for what it really cost.

## If something goes wrong

The troubleshooting table in [`cloud-deploy.md`](cloud-deploy.md) covers the common failures. When in doubt,
run `cloud-down` first and debug later: a broken environment bills exactly like a working one.
