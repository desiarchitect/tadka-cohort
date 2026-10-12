<#
.SYNOPSIS
  Run the whole evolution ladder (every checkpoint x scenario in docs/runbooks/evolution-1-to-1m.md), then build the report.

.DESCRIPTION
  Calls run-checkpoint.ps1 once per row below. Rows of the same day run back to back: the first resets and seeds
  the infrastructure, the following ones reuse it (-ReuseInfra -SkipBuild), so each checkpoint is seeded once.
  Orders run last on each day because they add rows. About 5 minutes per run plus setup: budget ~2.5 hours,
  ~7 hours with -Repeat 3. Re-running skips rows whose result file already exists unless you pass -Force.

.EXAMPLE
  pwsh evolution/run-ladder.ps1
.EXAMPLE
  pwsh evolution/run-ladder.ps1 -Repeat 3 -Only history
#>
param(
    [int]$Repeat = 1,
    [string]$Only = '',       # run just one scenario's rows, e.g. -Only history
    [string]$Rates = '',      # passed through to run-checkpoint.ps1 when set
    [int]$StepSeconds = 0,    # passed through when set
    [string]$WorktreeRoot = '', # passed through when set
    [switch]$Quick,           # passed through: class-length runs (~2 min each)
    [switch]$Force
)
$ErrorActionPreference = 'Stop'

# day, scenario, variant (in run order; a day's rows are contiguous)
$ladder = @(
    @('day-03', 'history', ''), @('day-03', 'hotkey', ''), @('day-03', 'browse', ''), @('day-03', 'orders', ''),
    @('day-05', 'history', ''), @('day-05', 'hotkey', ''),
    @('day-06', 'history', ''), @('day-06', 'hotkey', ''), @('day-06', 'browse', ''),
    @('day-07', 'orders', 'sync-unprotected'), @('day-07', 'orders', 'sync-slow'), @('day-07', 'orders', 'async-slow'),
    @('day-09', 'orders', 'payment-slow'), @('day-09', 'orders', 'payment-down'),
    @('day-16', 'history', ''), @('day-16', 'hotkey', ''), @('day-16', 'browse', ''),
    @('day-16', 'orders', 'payment-outage'), @('day-16', 'orders', 'payment-down'),
    @('day-16', 'history', 'replicas1'), @('day-16', 'history', 'replicas3')
)
if ($Only) { $ladder = @($ladder | Where-Object { $_[1] -eq $Only }) }
$results = Join-Path $PSScriptRoot 'results'

for ($i = 0; $i -lt $ladder.Count; $i++) {
    $day, $scenario, $variant = $ladder[$i]
    $label = "$day-$scenario" + $(if ($variant) { "-$variant" } else { '' }) + $(if ($Quick) { '-quick' } else { '' })
    $probe = if ($Repeat -gt 1) { "$label-r1.json" } else { "$label.json" }
    if (-not $Force -and (Test-Path (Join-Path $results $probe))) { Write-Host "skip $label (result exists; -Force to redo)"; continue }

    $prev = if ($i -gt 0) { $ladder[$i - 1] } else { $null }
    $next = if ($i -lt $ladder.Count - 1) { $ladder[$i + 1] } else { $null }
    $scaleOut = $variant -like 'replicas*'
    $prevScaleOut = $prev -and $prev[2] -like 'replicas*'
    # Reuse the day's infrastructure only between rows of the same day AND the same kind (scale-out has its own containers).
    $reuse = $prev -and $prev[0] -eq $day -and ($prevScaleOut -eq $scaleOut)
    $keep = $next -and $next[0] -eq $day -and (($next[2] -like 'replicas*') -eq $scaleOut)

    $argList = @{ Day = $day; Scenario = $scenario; Repeat = $Repeat }
    if ($variant) { $argList.Variant = $variant }
    if ($Rates) { $argList.Rates = $Rates }
    if ($StepSeconds -gt 0) { $argList.StepSeconds = $StepSeconds }
    if ($WorktreeRoot) { $argList.WorktreeRoot = $WorktreeRoot }
    if ($Quick) { $argList.Quick = $true }
    if ($reuse) { $argList.ReuseInfra = $true; $argList.SkipBuild = $true }
    if ($keep) { $argList.KeepInfra = $true }
    Write-Host "`n######## [$($i + 1)/$($ladder.Count)] $label" -ForegroundColor Magenta
    & (Join-Path $PSScriptRoot 'run-checkpoint.ps1') @argList
    if ($LASTEXITCODE -ne 0) { throw "run-checkpoint failed for $label" }
}

node (Join-Path $PSScriptRoot 'report.mjs')
