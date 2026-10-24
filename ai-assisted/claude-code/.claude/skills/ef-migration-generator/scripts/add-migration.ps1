# Adds an EF Core migration to ONE Tadka service.
# Works in Windows PowerShell 5.1 and PowerShell 7.
#   powershell -File add-migration.ps1 -Service Restaurant -MigrationName AddOperatingHours
#   powershell -File add-migration.ps1 -Service Payment -MigrationName AddRefundReason -WhatIf
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Orders', 'Payment', 'Delivery', 'Restaurant')]
    [string]$Service,

    # Letters, digits and underscores only, so the name can never become part of a shell command.
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]{2,60}$')]
    [string]$MigrationName
)

$map = @{
    Orders     = @{ Project = 'src/Tadka.Api';            Context = 'TadkaDbContext' }
    Payment    = @{ Project = 'src/Tadka.Payment.Api';    Context = 'PaymentDbContext' }
    Delivery   = @{ Project = 'src/Tadka.Delivery.Api';   Context = 'DeliveryDbContext' }
    Restaurant = @{ Project = 'src/Tadka.Restaurant.Api'; Context = 'RestaurantDbContext' }
}
$project = $map[$Service].Project
$context = $map[$Service].Context

if (-not (Test-Path $project)) {
    throw "Project folder '$project' not found. Run this from the repository root."
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet is not on PATH."
}

Write-Host "Service: $Service   Project: $project   Context: $context" -ForegroundColor Cyan
Write-Host "dotnet ef migrations add $MigrationName --project $project --startup-project $project --context $context" -ForegroundColor Gray

if ($PSCmdlet.ShouldProcess("$project ($context)", "add migration $MigrationName")) {
    & dotnet ef migrations add $MigrationName --project $project --startup-project $project --context $context
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Migration failed (exit code $LASTEXITCODE). If 'dotnet ef' is missing, run: dotnet tool install --global dotnet-ef" -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host "Done. Open the new file in $project/Migrations and read it before you commit." -ForegroundColor Green
}
