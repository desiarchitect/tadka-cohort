# Tadka — no-install load harness (Day 5/6 demos).
# Fires N requests, C at a time, against a URL; prints error-rate + p50/p95/p99 latency.
# A zero-dependency stand-in for k6 so anyone can reproduce the before/after on Windows.
#   pwsh scripts/measure-load.ps1 -Url http://localhost:5224/api/v1/restaurants/<id>/menu -Concurrency 30 -Total 600 -Label "menu, pool=10"
#
# The RATIO/shape (before vs after) is the lesson; absolute ms on a laptop will vary.

param(
    [string]$Url = "http://localhost:5224/api/v1/restaurants?page=1&pageSize=10",
    [int]$Concurrency = 20,
    [int]$Total = 400,
    [string]$Method = "GET",
    [string]$Body = $null,
    [string]$Label = "load"
)

$pool = [runspacefactory]::CreateRunspacePool(1, $Concurrency)
$pool.Open()

$work = {
    param($u, $m, $b)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        if ($m -eq "GET") {
            Invoke-WebRequest $u -UseBasicParsing -TimeoutSec 30 | Out-Null
        } else {
            Invoke-WebRequest $u -UseBasicParsing -Method $m -Body $b -ContentType "application/json" -TimeoutSec 30 | Out-Null
        }
        $sw.Stop(); [pscustomobject]@{ ms = $sw.Elapsed.TotalMilliseconds; ok = $true }
    } catch {
        $sw.Stop(); [pscustomobject]@{ ms = $sw.Elapsed.TotalMilliseconds; ok = $false }
    }
}

$jobs = @()
for ($i = 0; $i -lt $Total; $i++) {
    $ps = [powershell]::Create(); $ps.RunspacePool = $pool
    [void]$ps.AddScript($work).AddArgument($Url).AddArgument($Method).AddArgument($Body)
    $jobs += [pscustomobject]@{ ps = $ps; handle = $ps.BeginInvoke() }
}
$results = foreach ($j in $jobs) { $j.ps.EndInvoke($j.handle); $j.ps.Dispose() }
$pool.Close()

$ok  = @($results | Where-Object { $_.ok })
$lat = @($ok | Select-Object -ExpandProperty ms | Sort-Object)
function Pct($p) { if ($lat.Count -eq 0) { return 0 }; $idx = [int][math]::Ceiling($p / 100 * $lat.Count) - 1; return $lat[[math]::Max(0, [math]::Min($lat.Count - 1, $idx))] }
$errPct = if ($results.Count) { [math]::Round((1 - $ok.Count / $results.Count) * 100, 1) } else { 0 }

"[{0}] total={1} concurrency={2} errors={3}% p50={4}ms p95={5}ms p99={6}ms" -f `
    $Label, $results.Count, $Concurrency, $errPct, [math]::Round((Pct 50)), [math]::Round((Pct 95)), [math]::Round((Pct 99))
