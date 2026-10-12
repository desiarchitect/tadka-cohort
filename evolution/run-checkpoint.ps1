<#
.SYNOPSIS
  Run ONE scenario of the evolution workload against ONE checkpoint branch (day-03 ... day-16).

.DESCRIPTION
  The same k6 workload (k6/evolution.js) is run against each checkpoint, so the only variable between two
  runs is the architecture. For the chosen checkpoint this script:
    1. checks out origin/<day> into a git worktree next to this repo (../tadka-evo/<day>), leaving your
       own checkout untouched;
    2. resets the infrastructure (docker compose, project name "tadka-evo", volumes wiped) and waits for
       the containers the checkpoint needs to report healthy;
    3. builds the solution and starts that day's services with the rate limiters raised (so you measure
       capacity, not a limiter), each on its usual port;
    4. seeds the SAME ~200k-order table on every checkpoint (evolution/seed/orders-large.sql);
    5. logs in as Priya when the checkpoint has auth (Day 10+), clears the hot menu key for "hotkey";
    6. runs k6 and writes evolution/results/<label>.json (+ .meta.json), then stops the services.
  See docs/runbooks/evolution-1-to-1m.md for the full ladder and how to read the results.

  Needs: PowerShell 7 (pwsh), git, Docker, the .NET 10 SDK, k6. Stop your own Tadka stack first
  (docker compose down) - the compose files use fixed container names, so two stacks cannot coexist.

.EXAMPLE
  pwsh evolution/run-checkpoint.ps1 -Day day-03 -Scenario history
.EXAMPLE
  pwsh evolution/run-checkpoint.ps1 -Day day-07 -Scenario orders -Variant sync-slow
.EXAMPLE
  pwsh evolution/run-checkpoint.ps1 -Day day-16 -Scenario history -Variant replicas3 -Repeat 3
#>
param(
    [Parameter(Mandatory)] [string]$Day,
    [Parameter(Mandatory)] [ValidateSet('browse', 'history', 'hotkey', 'orders')] [string]$Scenario,
    [string]$Variant = '',
    [int]$Repeat = 1,
    [string]$Rates = '5,10,25,50,100,200,400',
    [int]$StepSeconds = 40,
    [string]$WorktreeRoot = '',
    [switch]$KeepInfra,   # leave the containers (and seeded data) up afterwards, e.g. to run another scenario on the same day
    [switch]$ReuseInfra,  # skip the reset/seed, reuse what -KeepInfra left behind (same day only)
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$Harness = Split-Path -Parent $PSScriptRoot                      # this repo (the day-16 checkout)
$Manifest = Get-Content (Join-Path $PSScriptRoot 'checkpoints.json') -Raw | ConvertFrom-Json
$Project = 'tadka-evo'
if (-not $WorktreeRoot) { $WorktreeRoot = Join-Path (Split-Path -Parent $Harness) 'tadka-evo' }

function Step($msg) { Write-Host "`n== $msg" -ForegroundColor Cyan }
function Fail($msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

# ---- resolve the checkpoint and variant ----------------------------------------------------------------
$cp = $Manifest.checkpoints.$Day
if (-not $cp) { Fail "unknown checkpoint '$Day'. Known: $(($Manifest.checkpoints.PSObject.Properties.Name) -join ', ')" }
$var = $null
if ($Variant) {
    $var = $cp.variants.$Variant
    $known = @($cp.variants.PSObject.Properties.Name) -join ', '
    if (-not $var) { Fail "checkpoint $Day has no variant '$Variant'. Known: $(if ($known) { $known } else { '(none)' })" }
}
$scaleOut = if ($var -and $var.scaleOut) { $var.scaleOut } else { $null }
$baseUrl = if ($scaleOut) { $scaleOut.baseUrl } else { $cp.baseUrl }
$labelBase = "$Day-$Scenario" + $(if ($Variant) { "-$Variant" } else { '' })
$resultsDir = Join-Path $Harness 'evolution/results'
$logDir = Join-Path $Harness "evolution/logs/$labelBase"
New-Item -ItemType Directory -Force -Path $resultsDir, $logDir | Out-Null

# ---- preflight -----------------------------------------------------------------------------------------
Step "Preflight ($labelBase)"
foreach ($tool in 'git', 'docker', 'k6') { if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Fail "$tool not found on PATH" } }
if (-not $scaleOut -and -not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'dotnet (the .NET 10 SDK) not found on PATH' }
docker info *> $null; if ($LASTEXITCODE -ne 0) { Fail 'Docker is not running' }
$owner = docker ps -a --filter 'name=^tadka-postgres$' --format '{{ index .Labels "com.docker.compose.project" }}' 2>$null
if ($owner -and $owner -ne $Project) {
    Fail "container tadka-postgres belongs to compose project '$owner'. Stop that stack first (docker compose down in that folder)."
}
function Test-Port([int]$port) {
    $c = [System.Net.Sockets.TcpClient]::new()
    try { return $c.ConnectAsync('localhost', $port).Wait(500) } catch { return $false } finally { $c.Dispose() }
}
if (-not $scaleOut -and -not $ReuseInfra) {
    foreach ($svc in $cp.services) {
        if (Test-Port $svc.port) { Fail "port $($svc.port) is already in use; stop whatever is running there (another dotnet run?)" }
    }
}

# ---- worktree ------------------------------------------------------------------------------------------
Step "Worktree for $Day"
$wt = Join-Path $WorktreeRoot $Day
git -C $Harness fetch --quiet origin $Day
if ($LASTEXITCODE -ne 0) { Fail "git fetch origin $Day failed" }
if (Test-Path (Join-Path $wt '.git')) {
    git -C $wt checkout --quiet --detach "origin/$Day"
} else {
    New-Item -ItemType Directory -Force -Path $WorktreeRoot | Out-Null
    git -C $Harness worktree add --quiet --detach $wt "origin/$Day"
}
if ($LASTEXITCODE -ne 0) { Fail "could not check out origin/$Day into $wt" }
$sha = (git -C $wt rev-parse --short HEAD).Trim()
Write-Host "  $wt @ $sha"

function Wait-Healthy([string[]]$names, [int]$timeoutSec = 240) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    foreach ($n in $names) {
        while ($true) {
            $state = docker inspect $n --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' 2>$null
            if ($state -eq 'healthy' -or $state -eq 'running') { Write-Host "  $n $state"; break }
            if ((Get-Date) -gt $deadline) { Fail "$n did not become healthy (last: $state)" }
            Start-Sleep -Seconds 3
        }
    }
}

function Wait-Http([string]$url, [int]$timeoutSec = 180) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ($true) {
        try { $r = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 5; if ($r.StatusCode -lt 500) { return } } catch { }
        if ((Get-Date) -gt $deadline) { Fail "$url did not answer within $timeoutSec s" }
        Start-Sleep -Seconds 2
    }
}

# ---- infrastructure ------------------------------------------------------------------------------------
Push-Location $wt
$started = @()
try {
    if (-not $ReuseInfra) {
        Step 'Reset infrastructure (docker compose down -v, up -d)'
        docker compose -p $Project down -v --remove-orphans *> $null
        if ($scaleOut) {
            docker compose -p $Project --profile scale-out up -d --build
        } else {
            docker compose -p $Project up -d
        }
        if ($LASTEXITCODE -ne 0) { Fail 'docker compose up failed' }
    }
    Wait-Healthy $cp.waitHealthy

    # ---- services --------------------------------------------------------------------------------------
    if ($scaleOut) {
        Step "Scale-out: running $($scaleOut.running -join ', ')"
        docker compose -p $Project --profile scale-out start @($scaleOut.running) | Out-Null
        Wait-Http "$baseUrl/health"
        if ($scaleOut.stopped.Count -gt 0) { docker compose -p $Project --profile scale-out stop @($scaleOut.stopped) | Out-Null }
        Start-Sleep -Seconds 5
        Wait-Http "$baseUrl/health"
    } else {
        if (-not $SkipBuild) {
            Step 'dotnet build Tadka.slnx'
            dotnet build Tadka.slnx -c Debug --nologo -v q | Out-Host
            if ($LASTEXITCODE -ne 0) { Fail 'build failed' }
        }
        $skip = if ($var -and $var.skip) { @($var.skip) } else { @() }
        foreach ($svc in $cp.services) {
            if ($skip -contains $svc.name) { Write-Host "  skipping $($svc.name) (variant $Variant)" -ForegroundColor Yellow; continue }
            # Child processes inherit this process's environment: set the service's variables, start it, restore.
            $vars = [ordered]@{ ASPNETCORE_ENVIRONMENT = 'Development'; ASPNETCORE_URLS = "http://localhost:$($svc.port)" }
            if ($svc.env) { foreach ($p in $svc.env.PSObject.Properties) { $vars[$p.Name] = [string]$p.Value } }
            if ($var -and $var.env -and $var.env.($svc.name)) {
                foreach ($p in $var.env.($svc.name).PSObject.Properties) { $vars[$p.Name] = [string]$p.Value }
            }
            $saved = @{}
            foreach ($k in $vars.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $vars[$k]) }
            $log = Join-Path $logDir "$($svc.name).log"
            $proc = Start-Process dotnet -ArgumentList @('run', '--no-build', '--no-launch-profile', '--project', $svc.project) `
                -WorkingDirectory $wt -NoNewWindow -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
            foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
            $started += $proc
            Write-Host "  started $($svc.name) on :$($svc.port) (log: $log)"
        }
        foreach ($svc in $cp.services) {
            if ($skip -contains $svc.name) { continue }
            Wait-Http "http://localhost:$($svc.port)/health"
            Write-Host "  $($svc.name) /health OK"
        }
    }

    # ---- seed --------------------------------------------------------------------------------------------
    if (-not $ReuseInfra) {
        Step 'Seed ~200k orders (same data on every checkpoint)'
        Get-Content (Join-Path $Harness $Manifest.seed) -Raw | docker exec -i tadka-postgres psql -U tadka -d tadka -v ON_ERROR_STOP=1 | Out-Host
        if ($LASTEXITCODE -ne 0) { Fail 'seed failed' }
        if (docker ps --filter 'name=^tadka-postgres-replica$' --format '{{.Names}}') {
            $deadline = (Get-Date).AddSeconds(90)
            while ([int](docker exec tadka-postgres-replica psql -U tadka -d tadka -tAc 'select count(*) from ordering.orders') -lt 150000) {
                if ((Get-Date) -gt $deadline) { Fail 'read replica did not catch up with the seed' }
                Start-Sleep -Seconds 2
            }
            Write-Host '  read replica caught up'
        }
    }

    # ---- run -------------------------------------------------------------------------------------------
    $k6Script = Join-Path $Harness 'k6/evolution.js'
    for ($i = 1; $i -le $Repeat; $i++) {
        $label = if ($Repeat -gt 1) { "$labelBase-r$i" } else { $labelBase }
        if ($Scenario -eq 'hotkey' -and (docker ps --filter 'name=^tadka-redis$' --format '{{.Names}}')) {
            docker exec tadka-redis redis-cli DEL $Manifest.hotKey | Out-Null   # start cold: the stampede is the point
        }
        $token = ''
        if ($cp.auth) {   # tokens live 15 min; log in fresh for every repeat
            $login = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/v1/auth/login" -ContentType 'application/json' `
                -Body ($Manifest.loginBody | ConvertTo-Json -Compress)
            $token = $login.accessToken
            if (-not $token) { Fail 'login returned no accessToken' }
        }
        Step "k6: $label  (base $baseUrl, rates $Rates, ${StepSeconds}s/step)"
        $k6Args = @('run', '-e', "BASE_URL=$baseUrl", '-e', "SCENARIO=$Scenario", '-e', "LABEL=$label",
            '-e', "OUT_DIR=$resultsDir", '-e', "RATES=$Rates", '-e', "STEP_SECONDS=$StepSeconds")
        if ($token) { $k6Args += @('-e', "TOKEN=$token") }
        & k6 @k6Args $k6Script
        # k6 exits non-zero only on script errors here (the per-step thresholds never fail).
        if ($LASTEXITCODE -ne 0 -and -not (Test-Path (Join-Path $resultsDir "$label.json"))) { Fail "k6 failed for $label" }

        [ordered]@{
            label = $label; day = $Day; commit = $sha; scenario = $Scenario; variant = $Variant
            title = $cp.title; adrs = $cp.adrs; note = $(if ($var) { $var.note } else { '' })
            replicas = $(if ($scaleOut) { $scaleOut.running.Count } else { 1 })
            machine = [ordered]@{ os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
                cores = [Environment]::ProcessorCount }
            ranAt = (Get-Date).ToString('o')
        } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $resultsDir "$label.meta.json")
    }
}
finally {
    foreach ($p in $started) { try { if (-not $p.HasExited) { $p.Kill($true) } } catch { } }
    if (-not $KeepInfra) {
        docker compose -p $Project --profile scale-out down -v --remove-orphans *> $null
    }
    Pop-Location
}

Write-Host "`nDone. Results in evolution/results/. Build the comparison with: node evolution/report.mjs" -ForegroundColor Green
exit 0
