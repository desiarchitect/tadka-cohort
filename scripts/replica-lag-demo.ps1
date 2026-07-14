<#
.SYNOPSIS
  Day 5 / ADR-016 demo: reproduce read-replica lag on demand, standalone (not as a side-effect of an
  unrelated backfill run). Hammers the PRIMARY with a write burst and watches streaming replication
  lag rise on tadka-postgres-replica, then fall back to ~0 once the burst stops.

.DESCRIPTION
  Writes WriteCount single-row UPDATEs against ordering.orders on the primary as fast as Postgres will
  take them (no think-time), then polls pg_stat_replication's replay_lag on the primary every 500ms for
  WatchSeconds so the room watches the number rise and recover live. Uses `docker exec psql`, so no host
  psql client is needed.

.PARAMETER WriteCount   Rows to touch on the primary (default 5000).
.PARAMETER WatchSeconds How long to keep polling lag after the burst (default 20).
.EXAMPLE
  ./scripts/replica-lag-demo.ps1 -WriteCount 20000 -WatchSeconds 30
#>
param(
    [int]$WriteCount = 5000,
    [int]$WatchSeconds = 20
)

$ErrorActionPreference = "Stop"
$primary = "tadka-postgres"

function Get-LagMs {
    $v = "SELECT COALESCE(MAX(EXTRACT(MILLISECONDS FROM replay_lag)),0)::int FROM pg_stat_replication;" |
        docker exec -i $primary psql -U tadka -d tadka -t -A 2>$null
    return [int]($v.Trim())
}

function Get-OrderCount {
    $v = "SELECT COUNT(*) FROM ordering.orders;" | docker exec -i $primary psql -U tadka -d tadka -t -A 2>$null
    return [int]($v.Trim())
}

Write-Host "=== Replica-lag repro (Day 5, ADR-016) ==="

$orderCount = Get-OrderCount
if ($orderCount -eq 0) {
    Write-Host "ordering.orders is empty - place a few orders first (POST /api/v1/orders via :8080), then re-run."
    exit 0
}
Write-Host "Baseline replica lag: $(Get-LagMs)ms"
Write-Host "Firing $WriteCount UPDATEs against the primary's existing orders (no think-time)..."

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$sql = @"
DO `$`$
DECLARE
    i INT;
    oid UUID;
BEGIN
    FOR i IN 1..$WriteCount LOOP
        SELECT "Id" INTO oid FROM ordering.orders OFFSET floor(random() * $orderCount) LIMIT 1;
        -- A no-op self-touch (Status set to its own value): generates real WAL/replication traffic
        -- without changing any order data, so this is safe to run against a demo database repeatedly.
        UPDATE ordering.orders SET "Status" = "Status" WHERE "Id" = oid;
    END LOOP;
END
`$`$;
"@
$sql | docker exec -i $primary psql -U tadka -d tadka -q 2>$null
$sw.Stop()

Write-Host "Burst done in $([int]$sw.Elapsed.TotalMilliseconds)ms. Watching replica lag for ${WatchSeconds}s..."
Write-Host ""

$deadline = (Get-Date).AddSeconds($WatchSeconds)
$peak = 0
while ((Get-Date) -lt $deadline) {
    $lag = Get-LagMs
    if ($lag -gt $peak) { $peak = $lag }
    Write-Host ("  replica-lag {0,5}ms" -f $lag)
    Start-Sleep -Milliseconds 500
}

Write-Host ""
Write-Host "Peak observed lag: ${peak}ms (Day-5's captured number was ~236ms on a laptop - yours will vary with hardware/load)."
Write-Host "Teaching point: any read that hit the replica during the peak could have served a stale order status."
Write-Host "Fix (ADR-016): read-your-own-writes goes to the PRIMARY for the request that just wrote; everything else can read the replica."
