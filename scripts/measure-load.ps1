# Concurrent GET harness for Day 5 pool beat. No k6. Works in Windows PowerShell 5.1.
# Usage (repo root, API running):
#   powershell -File scripts/measure-load.ps1 -Url "http://localhost:5224/api/v1/orders?customerId=00000000-0000-0000-0000-000000000000&pageSize=10" -Concurrency 40 -Total 400

param(
    [Parameter(Mandatory = $true)]
    [string]$Url,
    [int]$Concurrency = 40,
    [int]$Total = 400
)

$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @"
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
public static class TadkaLoad {
  public static Tuple<double[], int> Run(string url, int n, int c) {
    var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var times = new ConcurrentBag<double>();
    var errors = 0;
    var sem = new SemaphoreSlim(c);
    var tasks = new Task[n];
    for (int i = 0; i < n; i++) {
      tasks[i] = Work(client, url, sem, times, () => Interlocked.Increment(ref errors));
    }
    Task.WaitAll(tasks);
    client.Dispose();
    return Tuple.Create(times.ToArray(), errors);
  }
  static async Task Work(HttpClient client, string url, SemaphoreSlim sem, ConcurrentBag<double> times, Func<int> onErr) {
    await sem.WaitAsync().ConfigureAwait(false);
    var sw = Stopwatch.StartNew();
    try {
      var r = await client.GetAsync(url).ConfigureAwait(false);
      await r.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
      if (!r.IsSuccessStatusCode) onErr();
    } catch {
      onErr();
    }
    sw.Stop();
    times.Add(sw.Elapsed.TotalMilliseconds);
    sem.Release();
  }
}
"@ -ReferencedAssemblies System.Net.Http

$pair = [TadkaLoad]::Run($Url, $Total, $Concurrency)
$sorted = $pair.Item1 | Sort-Object
$errors = $pair.Item2

function Pct([double[]]$arr, [double]$p) {
    if ($arr.Length -eq 0) { return 0 }
    $i = [Math]::Min($arr.Length - 1, [Math]::Max(0, [int][Math]::Ceiling($p * $arr.Length) - 1))
    return $arr[$i]
}

Write-Host ("n={0} concurrency={1} errors={2}" -f $sorted.Length, $Concurrency, $errors)
if ($sorted.Length -gt 0) {
    Write-Host ("p50={0:N1} ms  p95={1:N1} ms  p99={2:N1} ms" -f (Pct $sorted 0.50), (Pct $sorted 0.95), (Pct $sorted 0.99))
} else {
    Write-Host "No samples. Is the API running on that URL?"
}
