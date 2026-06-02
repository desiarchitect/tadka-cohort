# docs/demo-scripts/02-pgbouncer-connection-exhaustion.ps1
# This script fires 150 concurrent background requests to exhaust database connections.

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host " PgBouncer Connection Exhaustion Demo" -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

$url = "http://localhost:5224/api/v1/restaurants"

Write-Host "Firing 150 concurrent requests to $url..." -ForegroundColor Yellow

# We use runspaces for true concurrent background execution in PowerShell
$runspacePool = [runspacefactory]::CreateRunspacePool(1, 150)
$runspacePool.Open()
$jobs = @()

for ($i = 0; $i -lt 150; $i++) {
    $powershell = [powershell]::Create().AddScript({
        param($url)
        try {
            $response = Invoke-RestMethod -Uri $url -Method Get -ErrorAction Stop
            return "Success"
        } catch {
            return "Failed: $($_.Exception.Message)"
        }
    }).AddArgument($url)
    
    $powershell.RunspacePool = $runspacePool
    $jobs += [PSCustomObject]@{
        Run = $powershell
        Handle = $powershell.BeginInvoke()
    }
}

Write-Host "Waiting for all requests to finish (this may take a few seconds)..."
$results = @()
foreach ($job in $jobs) {
    $results += $job.Run.EndInvoke($job.Handle)
    $job.Run.Dispose()
}
$runspacePool.Close()

$successCount = ($results | Where-Object { $_ -eq "Success" }).Count
$failCount = ($results | Where-Object { $_ -match "Failed" }).Count

Write-Host "`n--- DEMO RESULTS ---" -ForegroundColor Cyan
Write-Host "Successful Requests : $successCount" -ForegroundColor Green
Write-Host "Failed Requests     : $failCount" -ForegroundColor Red

if ($failCount -gt 0) {
    Write-Host "`n[RESULT]: The database (or internal connection pool) crashed!" -ForegroundColor Red
    Write-Host "If you ran this directly against Postgres (Port 5432) with Pooling=false, Postgres was overwhelmed." -ForegroundColor Red
} else {
    Write-Host "`n[RESULT]: Flawless Victory!" -ForegroundColor Green
    Write-Host "If you ran this against PgBouncer (Port 6432), it successfully multiplexed all 150 requests through its internal pool of 20 connections!" -ForegroundColor Green
}
