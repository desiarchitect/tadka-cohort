# Concurrent GET harness for Day 5 pool beat. No k6 required.
# Usage (repo root, API running):
#   pwsh scripts/measure-load.ps1 -Url "http://localhost:5224/api/v1/orders?customerId=00000000-0000-0000-0000-000000000000&pageSize=10" -Concurrency 40 -Total 400

param(
    [Parameter(Mandatory = $true)]
    [string]$Url,
    [int]$Concurrency = 40,
    [int]$Total = 400
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$handler = New-Object System.Net.Http.HttpClientHandler
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(30)

$times = New-Object System.Collections.Concurrent.ConcurrentBag[double]
$errors = 0

function Invoke-One {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $resp = $client.GetAsync($Url).GetAwaiter().GetResult()
        [void]$resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        if (-not $resp.IsSuccessStatusCode) { [void][System.Threading.Interlocked]::Increment([ref]$errors) }
    } catch {
        [void][System.Threading.Interlocked]::Increment([ref]$errors)
    } finally {
        $sw.Stop()
        $times.Add($sw.Elapsed.TotalMilliseconds)
    }
}

$pending = New-Object System.Collections.Generic.List[System.Threading.Tasks.Task]
$started = 0
while ($started -lt $Total) {
    while ($pending.Count -ge $Concurrency) {
        [void][System.Threading.Tasks.Task]::WaitAny($pending.ToArray())
        $done = $pending | Where-Object { $_.IsCompleted }
        foreach ($t in $done) { [void]$pending.Remove($t) }
    }
    $pending.Add([System.Threading.Tasks.Task]::Run({ Invoke-One }))
    $started++
}
[void][System.Threading.Tasks.Task]::WaitAll($pending.ToArray())

$sorted = $times.ToArray() | Sort-Object
function Pct([double[]]$arr, [double]$p) {
    $i = [Math]::Min($arr.Length - 1, [Math]::Max(0, [int][Math]::Ceiling($p * $arr.Length) - 1))
    return $arr[$i]
}

Write-Host ("n={0} concurrency={1} errors={2}" -f $sorted.Length, $Concurrency, $errors)
Write-Host ("p50={0:N1} ms  p95={1:N1} ms  p99={2:N1} ms" -f (Pct $sorted 0.50), (Pct $sorted 0.95), (Pct $sorted 0.99))
$client.Dispose()
