param()

$root = "D:\work\cohort\wtc\day-16"
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:RateLimit__PerMinute = "100000"
$env:Gateway__RateLimitPerMinute = "100000"
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"

$svcs = @(
  @{ Name = "Api"; Dir = "$root\src\Tadka.Api"; Dll = "bin\Debug\net10.0\Tadka.Api.dll"; Url = "http://localhost:5224" },
  @{ Name = "Payment"; Dir = "$root\src\Tadka.Payment.Api"; Dll = "bin\Debug\net10.0\Tadka.Payment.Api.dll"; Url = "http://localhost:5240" },
  @{ Name = "Delivery"; Dir = "$root\src\Tadka.Delivery.Api"; Dll = "bin\Debug\net10.0\Tadka.Delivery.Api.dll"; Url = "http://localhost:5250" },
  @{ Name = "Restaurant"; Dir = "$root\src\Tadka.Restaurant.Api"; Dll = "bin\Debug\net10.0\Tadka.Restaurant.Api.dll"; Url = "http://localhost:5260" },
  @{ Name = "Gateway"; Dir = "$root\src\Tadka.Gateway"; Dll = "bin\Debug\net10.0\Tadka.Gateway.dll"; Url = "http://localhost:8080" }
)

$procs = @()
foreach ($s in $svcs) {
  $p = Start-Process dotnet -ArgumentList "$($s.Dll) --urls $($s.Url)" -WorkingDirectory $s.Dir -PassThru
  $procs += $p
  Write-Host "Started $($s.Name) on $($s.Url) (PID $($p.Id))"
}

try {
  while ($true) {
    Start-Sleep -Seconds 2
  }
} finally {
  Write-Host "Shutting down cluster..."
  foreach ($p in $procs) {
    try {
      if (-not $p.HasExited) {
        Stop-Process -Id $p.Id -Force
      }
    } catch { }
  }
}
