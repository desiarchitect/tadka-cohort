<#
.SYNOPSIS
  Tear down the live Azure session (ADR-064): terraform destroy -> confirm the resource group is gone.

.DESCRIPTION
  Everything the session created lives in ONE resource group, so this is the whole cleanup. GHCR images are
  outside Azure and survive. Run it right after class: a forgotten environment is the main cost risk.
  If terraform destroy fails (e.g. lost state), -Force falls back to deleting the resource group with az.

  Non-interactive by design: terraform runs with -input=false -auto-approve and az with --yes, so the
  auto-teardown scheduled task (cloud-up -AutoDownAfterHours) can run it unattended. On success it also
  removes that scheduled task.

.PARAMETER Force  Fall back to `az group delete` if terraform destroy fails.
.EXAMPLE
  ./scripts/cloud-down.ps1
  ./scripts/cloud-down.ps1 -Force
#>
param(
    [switch]$Force
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "cloud-common.ps1")

Assert-Tool "terraform" "Install Terraform >= 1.6"
Assert-Tool "az" "Install the Azure CLI"
Assert-AzureLogin

$rg = "rg-tadka-session"
try { $rg = (Get-TfOutputs)["resource_group"] } catch { Write-Host "No outputs in state; assuming $rg" -ForegroundColor Yellow }

# destroy needs the same variables apply had; cloud-up wrote them to a gitignored file.
if (-not (Test-Path $script:VarsFile)) {
    @{ budget_alert_emails = @("unused-on-destroy@example.com") } | ConvertTo-Json | Set-Content -Path $script:VarsFile -Encoding ascii
}

$started = Get-Date
Write-Host "=== Tadka cloud-down: destroying $rg ===" -ForegroundColor Cyan
$destroyed = $true
try {
    Invoke-Terraform @("destroy", "-input=false", "-auto-approve")
} catch {
    $destroyed = $false
    Write-Host "terraform destroy failed: $($_.Exception.Message)" -ForegroundColor Red
    if ($Force) {
        Write-Host "-Force: deleting resource group $rg with az (no Terraform)." -ForegroundColor Yellow
        az group delete --name $rg --yes --no-wait
    } else {
        Write-Host "Re-run with -Force to delete the resource group directly." -ForegroundColor Yellow
    }
}

$gone = Wait-Until { (az group exists --name $rg).Trim() -eq "false" } -TimeoutSec 1800 -EverySec 20 -What "resource group $rg to disappear"
$mins = ((Get-Date) - $started).TotalMinutes
if ($gone) {
    Write-Host ("`nResource group {0} is GONE ({1:N1} min). Billing stops." -f $rg, $mins) -ForegroundColor Green
    Remove-Item $script:VarsFile -ErrorAction SilentlyContinue
    # The backstop task (cloud-up -AutoDownAfterHours) is no longer needed. Removed only on success, so a
    # failed manual teardown keeps its backstop. (When the task itself runs this, it removes itself last.)
    Unregister-AutoDownTask
} else {
    Write-Host "`nResource group $rg STILL EXISTS. Check the portal now; it is still billing." -ForegroundColor Red
    exit 1
}
Write-Host "Cost Management shows the session's spend after ~24 h. Record it in docs/cost-model.md." -ForegroundColor Yellow
