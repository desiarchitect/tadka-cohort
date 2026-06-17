# Toydemo inventory + optional smoke runner
# Usage:
#   .\verify-all.ps1              # list all toys + primary commands
#   .\verify-all.ps1 -Run break   # run break mode for each toy (needs Docker for DB toys)
#   .\verify-all.ps1 -Run fix     # run fix mode for each toy

param(
    [ValidateSet('', 'break', 'fix')]
    [string]$Run = ''
)

$root = $PSScriptRoot
$toys = @(
    @{ Day = '03'; Name = 'cursor-pagination-toy'; Path = 'day-03-api-primitives/cursor-pagination-toy'; Entry = 'real-db.js'; Docker = $true },
    @{ Day = '03'; Name = 'grpc-vs-rest-toy'; Path = 'day-03-api-primitives/grpc-vs-rest-toy'; Entry = 'real-bench.js'; Docker = $false },
    @{ Day = '06'; Name = 'rate-limiter-toy'; Path = 'day-06-cache-realtime/rate-limiter-toy'; Entry = 'index.js'; Docker = $false },
    @{ Day = '06'; Name = 'hot-key-stampede-toy'; Path = 'day-06-cache-realtime/hot-key-stampede-toy'; Entry = 'real-redis.js'; Docker = $true },
    @{ Day = '06'; Name = 'stateful-websocket-toy'; Path = 'day-06-cache-realtime/stateful-websocket-toy'; Entry = 'real-chat.js'; Docker = $true },
    @{ Day = '09'; Name = 'notification-fanout-toy'; Path = 'day-09-kafka-async/notification-fanout-toy'; Entry = 'real-kafka.js'; Docker = $true },
    @{ Day = '09'; Name = 'stream-processing-toy'; Path = 'day-09-kafka-async/stream-processing-toy'; Entry = 'index.js'; Docker = $false },
    @{ Day = '15'; Name = 'search-index-toy'; Path = 'day-15-breadth/search-index-toy'; Entry = 'real-db.js'; Docker = $true },
    @{ Day = '15'; Name = 'video-hls-cdn-toy'; Path = 'day-15-breadth/video-hls-cdn-toy'; Entry = 'real-demo.js'; Docker = $false; Setup = 'node setup-media.js' },
    @{ Day = '15'; Name = 'olap-cdc-toy'; Path = 'day-15-breadth/olap-cdc-toy'; Entry = 'real-db.js'; Docker = $true },
    @{ Day = '15'; Name = 'object-storage-toy'; Path = 'day-15-breadth/object-storage-toy'; Entry = 'real-demo.js'; Docker = $true },
    @{ Day = '15'; Name = 'web-crawler-toy'; Path = 'day-15-breadth/web-crawler-toy'; Entry = 'real-crawl.js'; Docker = $false }
)

Write-Host "=== Tadka Toy Demos ($($toys.Count) toys) ===" -ForegroundColor Cyan
Write-Host "Plan: toydemo/TOY-DEMO-PLAN.md  |  Index: toydemo/README.md`n"

if ($Run -ne '' -and ($toys | Where-Object { $_.Docker }).Count -gt 0) {
    Write-Host "Ensure Docker services are up from tadka/: docker compose up -d postgres redis kafka" -ForegroundColor Yellow
}

foreach ($t in $toys) {
    $dir = Join-Path $root $t.Path
    $cmd = "node $($t.Entry) --mode=$Run"
    if ($Run -eq '' -and $t.Entry -eq 'real-bench.js') { $cmd = 'node real-bench.js' }
    if ($Run -eq '' -and $t.Entry -eq 'index.js' -and $t.Name -eq 'rate-limiter-toy') { $cmd = 'node index.js --mode=break; node index.js --mode=fix' }

    Write-Host "[Day $($t.Day)] $($t.Name)" -ForegroundColor Green
    Write-Host "  cd toydemo\$($t.Path)"
    if ($t.Setup) { Write-Host "  $($t.Setup)" }
    Write-Host "  $cmd"
    Write-Host "  doc: RUN-AND-TEST.md`n"

    if ($Run -ne '') {
        Push-Location $dir
        try {
            if ($t.Setup) { Invoke-Expression $t.Setup }
            if ($t.Entry -eq 'real-bench.js') {
                node real-bench.js
            } else {
                node $t.Entry --mode=$Run
            }
            Write-Host "  OK`n" -ForegroundColor DarkGreen
        } catch {
            Write-Host "  FAILED: $_`n" -ForegroundColor Red
        } finally {
            Pop-Location
        }
    }
}