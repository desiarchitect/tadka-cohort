<#
.SYNOPSIS
  Shared helpers for cloud-up.ps1 / cloud-down.ps1 / cloud-failover.ps1 (ADR-064). Dot-source only.
#>

$script:RepoRoot  = Split-Path -Parent $PSScriptRoot
$script:AzureDir  = Join-Path $script:RepoRoot "deploy\azure"
$script:VarsFile  = Join-Path $script:AzureDir "session.auto.tfvars.json"

function Assert-Tool([string]$Name, [string]$InstallHint) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "'$Name' is not on PATH. $InstallHint (see docs/runbooks/cloud-deploy.md)."
    }
}

function Assert-AzureLogin {
    $sub = az account show --query id -o tsv 2>$null
    if (-not $sub) { throw "Not logged in to Azure. Run 'az login' (and 'az account set -s <subscription>') first." }
    $env:ARM_SUBSCRIPTION_ID = $sub.Trim()
    $name = az account show --query name -o tsv
    Write-Host "Azure subscription: $name ($($env:ARM_SUBSCRIPTION_ID))"
}

function Invoke-Terraform([string[]]$TfArgs) {
    # Terraform writes its error text to stderr. Under $ErrorActionPreference = "Stop" (the scripts' default)
    # PowerShell 5.1 turns the FIRST stderr line into a terminating error, so the real message is cut off and
    # only "terraform apply failed" survives. Run it with Continue and print every line, then check the exit
    # code ourselves.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & terraform "-chdir=$script:AzureDir" @TfArgs 2>&1 | ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { Write-Host $_.Exception.Message } else { Write-Host "$_" } }
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $prev }
    if ($code -ne 0) { throw "terraform $($TfArgs[0]) failed (exit $code). The error text is printed above." }
}

function Get-TfOutputs {
    $json = & terraform "-chdir=$script:AzureDir" output -json
    if ($LASTEXITCODE -ne 0 -or -not $json) { throw "No terraform outputs. Has cloud-up.ps1 run?" }
    $o = ($json | Out-String) | ConvertFrom-Json
    $h = @{}
    foreach ($p in $o.PSObject.Properties) { $h[$p.Name] = $p.Value.value }
    return $h
}

# HTTP call that never throws: returns Status (0 = no response), Headers, Body, Ms.
function Invoke-Http {
    param(
        [string]$Method = "GET",
        [string]$Url,
        [hashtable]$Headers = @{},
        [string]$Body = $null,
        [int]$TimeoutSec = 30
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $p = @{ Method = $Method; Uri = $Url; Headers = $Headers; UseBasicParsing = $true; TimeoutSec = $TimeoutSec }
        if ($Body) { $p.Body = $Body; $p.ContentType = "application/json" }
        $r = Invoke-WebRequest @p
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Headers = $r.Headers; Body = $r.Content; Ms = $sw.ElapsedMilliseconds }
    } catch {
        $resp = $_.Exception.Response
        if ($resp) {
            return [pscustomobject]@{ Status = [int]$resp.StatusCode; Headers = $resp.Headers; Body = $null; Ms = $sw.ElapsedMilliseconds }
        }
        return [pscustomobject]@{ Status = 0; Headers = @{}; Body = $_.Exception.Message; Ms = $sw.ElapsedMilliseconds }
    }
}

function Get-HeaderValue($Headers, [string]$Name) {
    if (-not $Headers) { return $null }
    foreach ($k in $Headers.Keys) { if ($k -ieq $Name) { return ($Headers[$k] -join ",") } }
    return $null
}

# Origin lockdown (ADR-064): the gateway URL 403s anything without our Front Door id, except /health,
# /health/ready and the SSE path. Instructor scripts that call the gateway URL directly (health waits, the
# Day-14 failover loop, which must bypass the CDN cache) send the header themselves, exactly as Front Door
# would. Without it the failover loop would count 403s as successes (it only counts 5xx as errors).
function Get-OriginHeaders([hashtable]$Outputs) {
    $id = $Outputs["front_door_id"]
    if ($id) { return @{ "X-Azure-FDID" = [string]$id } }
    return @{}
}

# Backstop for a forgotten teardown (ADR-064): a one-time Windows scheduled task that runs cloud-down.ps1.
# cloud-up -AutoDownAfterHours registers it; a successful cloud-down removes it.
$script:AutoDownTaskName = "Tadka-cloud-down-backstop"
$script:AutoDownLog = Join-Path ([System.IO.Path]::GetTempPath()) "tadka-auto-down.log"

function Register-AutoDownTask([double]$Hours) {
    $downScript = Join-Path $PSScriptRoot "cloud-down.ps1"
    $at = (Get-Date).AddHours($Hours)
    # -NonInteractive: cloud-down never prompts (terraform -auto-approve, az --yes) and must not hang if
    # something unexpected asks. -Force: fall back to az group delete if terraform destroy fails.
    $psArgs = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command `"& '$downScript' -Force *>> '$script:AutoDownLog'`""
    $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $psArgs -WorkingDirectory $script:RepoRoot
    $trigger = New-ScheduledTaskTrigger -Once -At $at
    # Current user, interactive logon: no stored password. The laptop must be on and the user logged in
    # (the az login token lives in the user profile). StartWhenAvailable runs it as soon as possible if the
    # machine was asleep or off at the trigger time; WakeToRun asks Windows to wake it.
    $user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -WakeToRun -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 2)
    Register-ScheduledTask -TaskName $script:AutoDownTaskName -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Force `
        -Description "Tadka ADR-064 backstop: runs scripts/cloud-down.ps1 -Force once, in case the session was not torn down." | Out-Null
    return $at
}

function Unregister-AutoDownTask {
    $t = Get-ScheduledTask -TaskName $script:AutoDownTaskName -ErrorAction SilentlyContinue
    if ($t) {
        Unregister-ScheduledTask -TaskName $script:AutoDownTaskName -Confirm:$false
        Write-Host "Removed the auto-teardown scheduled task ($script:AutoDownTaskName)." -ForegroundColor DarkGray
    }
}

# Same demo user + order body as scripts/cohort-smoke.ps1 (seeded by AuthSeeder / the migrations).
function Get-DemoToken([string]$BaseUrl, [hashtable]$Headers = @{}) {
    $r = Invoke-Http -Method POST -Url "$BaseUrl/api/v1/auth/login" -Headers $Headers -Body '{"email":"priya@tadka.test","password":"Password123!"}'
    if ($r.Status -ne 200) { throw "Login failed: HTTP $($r.Status) $($r.Body)" }
    $j = $r.Body | ConvertFrom-Json
    $t = $j.accessToken; if (-not $t) { $t = $j.AccessToken }
    if (-not $t) { throw "Login did not return accessToken." }
    return $t
}

function New-DemoOrderBody {
    return (@{
        customerId      = "c1b2c3d4-0001-4000-8000-000000000001"
        restaurantId    = "a1b2c3d4-0001-4000-8000-000000000001"
        items           = @(@{ menuItemId = "b1b2c3d4-0001-4000-8000-000000000001"; quantity = 1 })
        deliveryAddress = @{ line1 = "cloud"; line2 = "session"; city = "Bangalore"; pincode = "560066"; latitude = 12.93; longitude = 77.61 }
    } | ConvertTo-Json -Depth 5)
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutSec, [int]$EverySec = 10, [string]$What = "condition") {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Write-Host "  ...waiting for $what" -ForegroundColor DarkGray
        Start-Sleep -Seconds $EverySec
    }
    return $false
}
# Azure needs each resource provider registered once per subscription. A fresh Free Trial has Microsoft.App
# (Container Apps) and Microsoft.Cdn (Front Door) unregistered, which fails apply with
# MissingSubscriptionRegistration. Registering is free and idempotent.
function Ensure-ResourceProviders {
    $namespaces = "Microsoft.App", "Microsoft.Cdn", "Microsoft.OperationalInsights", "Microsoft.DBforPostgreSQL",
                  "Microsoft.Insights", "Microsoft.Network", "Microsoft.Consumption"
    foreach ($ns in $namespaces) {
        $state = (az provider show --namespace $ns --query registrationState -o tsv 2>$null)
        if ($state -ne "Registered") {
            Write-Host "Registering resource provider $ns (was '$state')..." -ForegroundColor Yellow
            az provider register --namespace $ns --wait | Out-Null
        }
    }
}
