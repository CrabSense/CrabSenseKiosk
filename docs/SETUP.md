# CrabSense Kiosk - Hướng dẫn cài đặt

## 📋 Yêu cầu

- .NET 8.0 SDK ([Download](https://dotnet.microsoft.com/download/dotnet/8.0))
- SQL Server 2019+ hoặc SQL Server LocalDB
- Visual Studio 2022 / VS Code / Rider (tùy chọn)

## 🚀 Cài đặt nhanh

### Windows

```powershell
# 1. Cài đặt dependencies và tạo database
.\setup-database.ps1

# 2. Chạy ứng dụng
dotnet run
```

### Linux/Mac

```bash
# 1. Cấp quyền thực thi
chmod +x setup-database.sh

# 2. Cài đặt dependencies và tạo database
./setup-database.sh

# 3. Chạy ứng dụng
dotnet run
```

## 📝 Cài đặt thủ công

### 1. Restore packages

```bash
dotnet restore
```

### 2. Cài đặt EF Core Tools

```bash
# Cài đặt global tool
dotnet tool install --global dotnet-ef

# Hoặc update nếu đã cài
dotnet tool update --global dotnet-ef
```

### 3. Cấu hình Connection String

Mở `appsettings.json` và cập nhật connection string:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=(localdb)\\mssqllocaldb;Database=CrabSenseDb;Trusted_Connection=True;MultipleActiveResultSets=true"
  }
}
```

**Các options khác:**

**SQL Server LocalDB (Windows):**
```
Server=(localdb)\\mssqllocaldb;Database=CrabSenseDb;Trusted_Connection=True;
```

**SQL Server với authentication:**
```
Server=localhost;Database=CrabSenseDb;User Id=sa;Password=YourPassword;TrustServerCertificate=True;
```

**SQL Server trên mạng:**
```
Server=192.168.1.100;Database=CrabSenseDb;User Id=username;Password=password;TrustServerCertificate=True;
```

### 4. Tạo Migration

```bash
dotnet ef migrations add InitialCreate --output-dir Data/Migrations
```

### 5. Cập nhật Database

```bash
dotnet ef database update
```

### 6. Chạy ứng dụng

```bash
dotnet run
```

Truy cập:
- API: `https://localhost:5001`
- Swagger UI: `https://localhost:5001/`

## 🔧 Các lệnh hữu ích

### Database Migrations

```bash
# Tạo migration mới
dotnet ef migrations add MigrationName

# Cập nhật database
dotnet ef database update

# Rollback về migration trước
dotnet ef database update PreviousMigrationName

# Xóa migration cuối (chưa apply)
dotnet ef migrations remove

# Xem danh sách migrations
dotnet ef migrations list

# Tạo SQL script từ migrations
dotnet ef migrations script
```

### Build & Run

```bash
# Build project
dotnet build

# Run in development mode
dotnet run

# Run in production mode
dotnet run --environment Production

# Build for release
dotnet build -c Release

# Publish
dotnet publish -c Release -o ./publish
```

### Testing API

**Sử dụng curl:**

```bash
# Health check
curl https://localhost:5001/api/health

# Tạo sensor mới
curl -X POST https://localhost:5001/api/sensor \
  -H "Content-Type: application/json" \
  -d '{"name":"Sensor Test","sensorType":"Temperature","location":"Pool A"}'

# Tạo bản đọc chất lượng nước
curl -X POST https://localhost:5001/api/waterquality \
  -H "Content-Type: application/json" \
  -d '{"temperature":25.5,"pH":7.8,"dissolvedOxygen":6.5,"salinity":28.0}'
```

**Hoặc sử dụng Swagger UI tại:** `https://localhost:5001/`

## 🐛 Xử lý sự cố

### Lỗi "Could not execute dotnet-ef"

```bash
# Cài đặt EF Core tools
dotnet tool install --global dotnet-ef

# Thêm vào PATH (nếu cần)
export PATH="$PATH:$HOME/.dotnet/tools"  # Linux/Mac
# hoặc thêm %USERPROFILE%\.dotnet\tools vào PATH trong Windows
```

### Lỗi kết nối SQL Server

1. Kiểm tra SQL Server đã chạy
2. Kiểm tra connection string
3. Kiểm tra firewall
4. Với LocalDB: `sqllocaldb start mssqllocaldb`

### Lỗi certificate SSL

Chạy:
```bash
dotnet dev-certs https --trust
```

### Lỗi port đã được sử dụng

Thay đổi port trong `Properties/launchSettings.json` hoặc `appsettings.json`

## 📦 Dependencies

Các package chính:

- `Microsoft.EntityFrameworkCore.SqlServer` - Database ORM
- `Microsoft.EntityFrameworkCore.Design` - EF Core Tools
- `Swashbuckle.AspNetCore` - API Documentation
- `Serilog.AspNetCore` - Logging
- `FluentValidation.AspNetCore` - Validation

## 🎯 Tiếp theo

Sau khi cài đặt thành công:

1. Đọc [README.md](README.md) để hiểu kiến trúc hệ thống
2. Truy cập Swagger UI để xem tài liệu API
3. Kiểm tra logs tại thư mục `logs/`
4. Tích hợp với CrabSenseIot và CrabSenseAIWaterQualityAnalysis

## 📧 Hỗ trợ

Nếu gặp vấn đề, vui lòng:
- Kiểm tra logs tại `logs/crabsense-*.txt`
- Tạo issue trên GitHub
- Email: support@crabsense.com
