# Setup Database for CrabSense Kiosk
Write-Host "=== CrabSense Kiosk Database Setup ===" -ForegroundColor Cyan

# 1. Install EF Core tools if not already installed
Write-Host "`n[1/3] Checking EF Core tools..." -ForegroundColor Yellow
$efInstalled = dotnet tool list -g | Select-String "dotnet-ef"

if (-not $efInstalled) {
    Write-Host "Installing EF Core tools globally..." -ForegroundColor Green
    dotnet tool install --global dotnet-ef
} else {
    Write-Host "EF Core tools already installed." -ForegroundColor Green
    Write-Host "Updating to latest version..." -ForegroundColor Yellow
    dotnet tool update --global dotnet-ef
}

# 2. Create initial migration
Write-Host "`n[2/3] Creating initial migration..." -ForegroundColor Yellow
dotnet ef migrations add InitialCreate --output-dir Data/Migrations

# 3. Update database
Write-Host "`n[3/3] Updating database..." -ForegroundColor Yellow
dotnet ef database update

Write-Host "`n=== Setup Complete! ===" -ForegroundColor Green
Write-Host "Database is ready. You can now run the application with: dotnet run" -ForegroundColor Cyan
