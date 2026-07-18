#!/bin/bash

# Setup Database for CrabSense Kiosk
echo "=== CrabSense Kiosk Database Setup ==="

# 1. Install EF Core tools if not already installed
echo -e "\n[1/3] Checking EF Core tools..."
if ! dotnet tool list -g | grep -q "dotnet-ef"; then
    echo "Installing EF Core tools globally..."
    dotnet tool install --global dotnet-ef
else
    echo "EF Core tools already installed."
    echo "Updating to latest version..."
    dotnet tool update --global dotnet-ef
fi

# 2. Create initial migration
echo -e "\n[2/3] Creating initial migration..."
dotnet ef migrations add InitialCreate --output-dir Data/Migrations

# 3. Update database
echo -e "\n[3/3] Updating database..."
dotnet ef database update

echo -e "\n=== Setup Complete! ==="
echo "Database is ready. You can now run the application with: dotnet run"
