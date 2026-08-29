# Fire two PATCH /status Confirmed at the same time (not Start-Job).
# Usage (repo root, API running, FRESH Created order id):
#   .\docs\runbooks\race-status.ps1 -OrderId PASTE_ID
#
# Day 3 (no xmin): expect TWO HTTP 204 — both writers think they confirmed. That is the bug.
# Day 4 (xmin):     expect 204+409 (race) or 204+422 (serialised). Not two 204s.
#
# Do not use Confirm+Cancel: that sequence is legal, so two 204s is a normal cancel.

param(
    [Parameter(Mandatory = $true)]
    [string]$OrderId
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$url = "http://localhost:5224/api/v1/orders/$OrderId/status"
$json = Get-Content -Raw -Path (Join-Path $PSScriptRoot "status-confirmed.json")

function Start-Patch {
    $client = New-Object System.Net.Http.HttpClient
    $content = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, "application/json")
    $req = New-Object System.Net.Http.HttpRequestMessage
    $req.Method = New-Object System.Net.Http.HttpMethod "PATCH"
    $req.RequestUri = [Uri]$url
    $req.Content = $content
    return @{ Client = $client; Task = $client.SendAsync($req) }
}

$a = Start-Patch
$b = Start-Patch
[void][Threading.Tasks.Task]::WaitAll($a.Task, $b.Task)

function Show($label, $task) {
    $resp = $task.Result
    $body = $resp.Content.ReadAsStringAsync().Result
    Write-Host "$label HTTP $([int]$resp.StatusCode) $($resp.StatusCode)"
    if ($body) { Write-Host $body }
}

Show "A" $a.Task
Show "B" $b.Task
$a.Client.Dispose()
$b.Client.Dispose()
