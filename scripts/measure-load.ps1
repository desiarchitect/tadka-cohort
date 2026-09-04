# Concurrent GET harness for Day 5 pool beat. No k6. Works in Windows PowerShell 5.1.
# Usage (repo root, API running):
#   powershell -File scripts/measure-load.ps1 -Url "http://localhost:5224/api/v1/orders?customerId=00000000-0000-0000-0000-000000000000&pageSize=10" -Concurrency 40 -Total 400
# Collateral (menu / restaurant list during the same storm — shared pool):
#   ... -ProbeUrl "http://localhost:5224/api/v1/restaurants","http://localhost:5224/api/v1/restaurants/a1b2c3d4-0001-4000-8000-000000000001/menu"

param(
    [Parameter(Mandatory = $true)]
    [string]$Url,
    [int]$Concurrency = 40,
    [int]$Total = 400,
    [string[]]$ProbeUrl = @()
)

$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @"
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
public class TadkaCtr { public int V; }
public static class TadkaLoad {
  public static Tuple<double[], int, string> Run(string url, int n, int c, string[] probes) {
    ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, c + 64);
    var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var times = new ConcurrentBag<double>();
    var errors = new TadkaCtr();
    var sem = new SemaphoreSlim(c);
    var all = new List<Task>();
    for (int i = 0; i < n; i++) {
      all.Add(Work(client, url, sem, times, () => Interlocked.Increment(ref errors.V)));
    }
    var probeMeta = new List<Tuple<string, ConcurrentBag<double>, TadkaCtr>>();
    if (probes != null) {
      for (int pi = 0; pi < probes.Length; pi++) {
        var p = probes[pi];
        if (string.IsNullOrWhiteSpace(p)) continue;
        var pTimes = new ConcurrentBag<double>();
        var pErr = new TadkaCtr();
        var pSem = new SemaphoreSlim(2);
        for (int i = 0; i < 30; i++) {
          all.Add(Work(client, p, pSem, pTimes, () => Interlocked.Increment(ref pErr.V)));
        }
        probeMeta.Add(Tuple.Create(p, pTimes, pErr));
      }
    }
    Task.WaitAll(all.ToArray());
    client.Dispose();
    var sb = new StringBuilder();
    for (int i = 0; i < probeMeta.Count; i++) {
      var m = probeMeta[i];
      var arr = m.Item2.ToArray();
      Array.Sort(arr);
      sb.AppendLine(string.Format(
        "probe {0}  n={1} errors={2}  p50={3:N1} ms  p99={4:N1} ms",
        Label(m.Item1), arr.Length, m.Item3.V, Pct(arr, 0.50), Pct(arr, 0.99)));
    }
    return Tuple.Create(times.ToArray(), errors.V, sb.ToString());
  }
  static string Label(string url) {
    try {
      var u = new Uri(url);
      var s = u.AbsolutePath.TrimEnd('/');
      if (s.EndsWith("/menu", StringComparison.OrdinalIgnoreCase)) return "menu";
      var i = s.LastIndexOf('/');
      return i >= 0 ? s.Substring(i + 1) : s;
    } catch { return url; }
  }
  static double Pct(double[] arr, double p) {
    if (arr == null || arr.Length == 0) return 0;
    int i = Math.Min(arr.Length - 1, Math.Max(0, (int)Math.Ceiling(p * arr.Length) - 1));
    return arr[i];
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

$probes = @()
if ($ProbeUrl) { $probes = @($ProbeUrl) }

$pair = [TadkaLoad]::Run($Url, $Total, $Concurrency, [string[]]$probes)
$sorted = $pair.Item1 | Sort-Object
$errors = $pair.Item2
$probeLines = $pair.Item3

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
if ($probeLines) {
    Write-Host $probeLines.TrimEnd()
}
