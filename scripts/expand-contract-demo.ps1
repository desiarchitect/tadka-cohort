<#
.SYNOPSIS
  Day 12 / ADR-038 expand-contract dual-write demo for menu item Name â†’ DisplayName.

.DESCRIPTION
  Shows the production rename path WITHOUT downtime:
    1. Expand    -  DisplayName column already exists (migration AddDisplayName).
    2. Dual-write  -  Demo:DualWriteDisplayName=true; PATCH/POST name fills both columns.
    3. Backfill  -  this script copies Name â†’ DisplayName for historical rows (chunked).
    4. Switch-read  -  API already prefers DisplayName when set (MapItem).
    5. Contract   -  (documented only) stop writing Name, drop column in a later deploy.

  Giant-UPDATE break contrast: -BreakGiantUpdate seeds + runs one unthrottled UPDATE for lag demo.

.PARAMETER ChunkSize   Backfill batch size (default 200).
.PARAMETER BreakGiantUpdate  If set, also run the naive single-statement UPDATE on a seeded table for contrast.
.EXAMPLE
  ./scripts/expand-contract-demo.ps1
  ./scripts/expand-contract-demo.ps1 -BreakGiantUpdate
#>
param(
  [int]$ChunkSize = 200,
  [switch]$BreakGiantUpdate
)

$ErrorActionPreference = "Stop"
$srcContainer = "tadka-restaurant-db"
$usr = "tadka"
$srcDb = "tadka_restaurant"

function Sql([string]$sql) {
  $sql | docker exec -i $srcContainer psql -U $usr -d $srcDb -t -A -f -
}

Write-Host "== Expand-contract dual-write (ADR-038): Name -> DisplayName ==" -ForegroundColor Cyan

# Step 1: expand already applied by EF migration (nullable DisplayName).
$hasCol = (Sql "SELECT 1 FROM information_schema.columns WHERE table_schema='restaurant' AND table_name='menu_items' AND column_name='DisplayName';").Trim()
if ($hasCol -ne "1") {
  Write-Host "DisplayName column missing  -  start Restaurant.Api once so Migrate() applies AddDisplayName." -ForegroundColor Red
  exit 1
}
Write-Host "[expand] DisplayName column present (additive, non-breaking)." -ForegroundColor Green

# Step 3: backfill historical rows (Name -> DisplayName) in chunks  -  never one giant lock if avoidable.
$nullCount = [int]((Sql "SELECT count(*) FROM restaurant.menu_items WHERE `"DisplayName`" IS NULL;").Trim())
Write-Host "[backfill] rows with NULL DisplayName: $nullCount (chunk=$ChunkSize)"

$done = 0
while ($true) {
  $n = [int]((Sql @"
WITH batch AS (
  SELECT "Id" FROM restaurant.menu_items
  WHERE "DisplayName" IS NULL
  ORDER BY "Id"
  LIMIT $ChunkSize
  FOR UPDATE SKIP LOCKED
)
UPDATE restaurant.menu_items m
SET "DisplayName" = m."Name"
FROM batch b
WHERE m."Id" = b."Id"
RETURNING 1;
"@ | Measure-Object -Line).Lines)
  if ($n -le 0) { break }
  $done += $n
  Write-Host "  batch wrote ~$n rows (cumulative ~$done)"
  Start-Sleep -Milliseconds 50
}

$remaining = [int]((Sql "SELECT count(*) FROM restaurant.menu_items WHERE `"DisplayName`" IS NULL;").Trim())
Write-Host "[backfill] done. remaining NULL DisplayName: $remaining" -ForegroundColor Green
Write-Host "[switch-read] API MapItem already uses DisplayName ?? Name  -  no code flip required."
Write-Host "[contract] later deploy: stop writing Name, drop Name column. NOT done in this script."
Write-Host ""
Write-Host "Live dual-write: set Demo__DualWriteDisplayName=true on Restaurant.Api, then PATCH a menu name."
Write-Host "  docker exec $srcContainer psql -U $usr -d $srcDb -c 'SELECT `"Name`",`"DisplayName`" FROM restaurant.menu_items LIMIT 5;'"

if ($BreakGiantUpdate) {
  Write-Host ""
  Write-Host "== Contrast: naive giant UPDATE (the outage shape) ==" -ForegroundColor Yellow
  Write-Host "Seeding 20k synthetic rows then one UPDATE ... (watch replica lag if streaming is on)."
  Sql @"
INSERT INTO restaurant.menu_items ("Id","RestaurantId","Name","Category","IsAvailable","IsVeg","price","currency")
SELECT gen_random_uuid(),
       'a1b2c3d4-0001-4000-8000-000000000001',
       'Giant Update Dish ' || g,
       'LoadTest', true, false, 99.00, 'INR'
FROM generate_series(1, 20000) g;
"@ | Out-Null
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  Sql "UPDATE restaurant.menu_items SET `"DisplayName`" = `"Name`" WHERE `"Category`" = 'LoadTest';" | Out-Null
  $sw.Stop()
  Write-Host "Giant UPDATE finished in $($sw.ElapsedMilliseconds) ms  -  on a 50M-row hot table this locks + blows replica lag."
  Write-Host "Cleanup: DELETE FROM restaurant.menu_items WHERE `"Category`" = 'LoadTest';"
}
