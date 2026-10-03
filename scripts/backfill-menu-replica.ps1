<#
.SYNOPSIS
  Day 12 / ADR-038 demo: online, zero-downtime backfill of the monolith's local price replica
  (ordering.menu_replica) from the Restaurant service's database (restaurant.menu_items).

.DESCRIPTION
  When Restaurant is extracted (ADR-036), the monolith's read model (ADR-037) starts empty — events only
  carry FUTURE changes, so the CURRENT menu must be backfilled. In production that table is huge and the
  system is live, so we must NOT lock it or saturate the replica. This script demonstrates the discipline:

    * CHUNKED        — keyset pagination (WHERE "Id" > lastId ORDER BY "Id" LIMIT N); commit per batch.
    * PARTITIONED    — with -Workers N, worker i only takes rows where hash(Id) % N = i, so N copies of this
                       script (-Worker 0 .. N-1) take DISJOINT slices of the table and never touch the same row.
                       (This is a copy between two databases, so no row lock can be held across the read and the
                       upsert; disjointness comes from the partition, not from a lock.)
    * THROTTLED      — sleep between batches and WATCH REPLICATION LAG; back off when it grows (protect the
                       read replica, ADR-016).
    * IDEMPOTENT     — INSERT ... ON CONFLICT DO UPDATE (upsert), so a re-run resumes, never duplicates.
    * RESUMABLE      — every batch prints its high-water mark; pass it as -StartAfterId to resume after a crash.
    * NEVER LOCKS    — plain reads on the source and additive upserts on the target; nothing blocks live traffic.

  Runs entirely through `docker exec psql`, so no host psql client is needed.

.PARAMETER ChunkSize     Rows per batch (default 500).
.PARAMETER ThrottleMs    Sleep between batches in ms (default 200).
.PARAMETER LagCeilingMs  If replication lag exceeds this, back off (default 1000).
.PARAMETER SeedExtra     Optionally generate N synthetic menu items in restaurant-db first, to make the
                         backfill sizeable for the demo (default 0 = use the real seeded menu only).
.PARAMETER Workers       Total number of parallel workers (default 1 = a single worker takes everything).
.PARAMETER Worker        This worker's index, 0 .. Workers-1 (default 0).
.PARAMETER StartAfterId  Resume point: only rows with Id greater than this are copied (default: from the start).
.EXAMPLE
  ./scripts/backfill-menu-replica.ps1 -SeedExtra 50000 -ChunkSize 1000 -ThrottleMs 100
.EXAMPLE
  # two workers, in two terminals, taking disjoint halves of the table
  ./scripts/backfill-menu-replica.ps1 -Workers 2 -Worker 0
  ./scripts/backfill-menu-replica.ps1 -Workers 2 -Worker 1
#>
param(
  [int]$ChunkSize = 500,
  [int]$ThrottleMs = 200,
  [int]$LagCeilingMs = 1000,
  [int]$SeedExtra = 0,
  [int]$Workers = 1,
  [int]$Worker = 0,
  [string]$StartAfterId = "00000000-0000-0000-0000-000000000000"
)

if ($Workers -lt 1 -or $Worker -lt 0 -or $Worker -ge $Workers) {
  throw "-Worker must be between 0 and -Workers minus 1 (got Worker=$Worker, Workers=$Workers)."
}

$ErrorActionPreference = "Stop"
$srcContainer = "tadka-restaurant-db"   # Restaurant service DB (own DB, ADR-036)
$dstContainer = "tadka-postgres"        # monolith primary (owns ordering.menu_replica)
$primary      = "tadka-postgres"        # for replication-lag readout (ADR-016)
$srcDb = "tadka_restaurant"; $dstDb = "tadka"; $usr = "tadka"

# SQL is piped via STDIN (psql -f -), NOT `-c "..."`, because PowerShell mangles the double-quotes that
# Postgres needs around PascalCase identifiers ("Id", "PriceAmount", ...) when they're a native-exe argument.
function Src([string]$sql) { $sql | docker exec -i $srcContainer psql -U $usr -d $srcDb -t -A -F "|" -f - }
function Dst([string]$sql) { $sql | docker exec -i $dstContainer psql -U $usr -d $dstDb -t -A -f - | Out-Null }
function Lag() {
  $v = "SELECT COALESCE(MAX(EXTRACT(MILLISECONDS FROM replay_lag)),0)::int FROM pg_stat_replication;" |
    docker exec -i $primary psql -U $usr -d $dstDb -t -A -f - 2>$null
  if ($LASTEXITCODE -ne 0 -or -not $v) { return 0 } else { return [int]($v.Trim()) }
}

Write-Host "== Online backfill: restaurant.menu_items -> ordering.menu_replica (ADR-038) ==" -ForegroundColor Cyan

if ($SeedExtra -gt 0 -and $Worker -eq 0) {
  Write-Host "Seeding $SeedExtra synthetic menu items into restaurant-db (to make the backfill sizeable)..." -ForegroundColor Yellow
  $seedSql = @"
INSERT INTO restaurant.menu_items ("Id","RestaurantId","Name","Category","IsAvailable","IsVeg","price","currency")
SELECT gen_random_uuid(),
       'a1b2c3d4-0001-4000-8000-000000000001',
       'Load Test Dish ' || g,
       'LoadTest', true, (g % 2 = 0), (50 + (g % 400))::numeric(10,2), 'INR'
FROM generate_series(1, $SeedExtra) g;
"@
  $seedSql | docker exec -i $srcContainer psql -U $usr -d $srcDb -f - | Out-Null
}

# This worker's slice of the table: every row whose hashed Id lands in bucket $Worker of $Workers.
$slice = "abs(hashtext(mi.`"Id`"::text)::bigint) % $Workers = $Worker"
$total = [int]((Src "SELECT count(*) FROM restaurant.menu_items mi WHERE $slice AND mi.`"Id`" > '$StartAfterId';").Trim())
Write-Host "Worker $Worker of $Workers : source rows to backfill: $total  (chunk=$ChunkSize, throttle=${ThrottleMs}ms, lag-ceiling=${LagCeilingMs}ms)`n"

$lastId = $StartAfterId
$done = 0; $batchNo = 0; $sw = [System.Diagnostics.Stopwatch]::StartNew()

while ($true) {
  # Next keyset page of THIS worker's slice. Plain read, no row lock: the slice is what keeps workers disjoint.
  $rows = Src @"
SELECT mi."Id", mi."RestaurantId", mi."Name", mi."price", mi."currency", mi."IsAvailable"
FROM restaurant.menu_items mi
WHERE $slice AND mi."Id" > '$lastId'
ORDER BY mi."Id"
LIMIT $ChunkSize;
"@
  $rows = @($rows | Where-Object { $_ -and $_.Trim() -ne "" })
  if ($rows.Count -eq 0) { break }

  # Build one idempotent upsert for the batch (INSERT ... ON CONFLICT DO UPDATE).
  $values = foreach ($r in $rows) {
    $c = $r.Split("|")
    $id=$c[0]; $rid=$c[1]; $name=$c[2].Replace("'","''"); $price=$c[3]; $cur=$c[4]; $avail = ($c[5] -eq 't')
    $lastId = $id
    "('$id','$rid','$name',$price,'$cur',$($avail.ToString().ToLower()),NOW())"
  }
  $upsert = @"
INSERT INTO ordering.menu_replica ("MenuItemId","RestaurantId","Name","PriceAmount","PriceCurrency","IsAvailable","UpdatedAt")
VALUES $($values -join ",")
ON CONFLICT ("MenuItemId") DO UPDATE
  SET "Name"=EXCLUDED."Name", "PriceAmount"=EXCLUDED."PriceAmount",
      "PriceCurrency"=EXCLUDED."PriceCurrency", "IsAvailable"=EXCLUDED."IsAvailable", "UpdatedAt"=NOW();
"@
  Dst $upsert
  $done += $rows.Count; $batchNo++

  # Throttle + watch replication lag; back off if the replica is falling behind (ADR-016).
  $lag = Lag
  $pct = if ($total -gt 0) { [int]($done * 100 / $total) } else { 100 }
  Write-Host ("batch {0,4}  +{1,-4} rows  total {2,7}/{3} ({4,3}%)  replica-lag {5,5}ms  high-water {6}" -f $batchNo, $rows.Count, $done, $total, $pct, $lag, $lastId)

  if ($lag -gt $LagCeilingMs) {
    $backoff = $ThrottleMs * 5
    Write-Host ("  lag {0}ms > ceiling {1}ms - backing off {2}ms" -f $lag, $LagCeilingMs, $backoff) -ForegroundColor Yellow
    Start-Sleep -Milliseconds $backoff
  } else {
    Start-Sleep -Milliseconds $ThrottleMs
  }
}

$sw.Stop()
$replicaCount = [int]((docker exec $dstContainer psql -U $usr -d $dstDb -t -A -c "SELECT count(*) FROM ordering.menu_replica;").Trim())
Write-Host "`nWorker $Worker of $Workers done. Backfilled $done row(s) in $batchNo batch(es) over $([int]$sw.Elapsed.TotalSeconds)s." -ForegroundColor Green
Write-Host "ordering.menu_replica now holds $replicaCount row(s). Reads never blocked; table never locked." -ForegroundColor Green
Write-Host "From here, menu-updated events keep the replica fresh (ADR-037)." -ForegroundColor Green
