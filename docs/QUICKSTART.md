# 🚀 Quick Start Guide - CrabSense Kiosk API

Hướng dẫn nhanh để chạy API trong 5 phút!

## ⚡ Bắt đầu nhanh

### Windows

```powershell
# 1. Clone và vào thư mục
cd d:\CrabSense\CrabSenseKiosk

# 2. Setup database (chạy script tự động)
.\setup-database.ps1

# 3. Chạy API
dotnet run

# 4. Mở trình duyệt
start https://localhost:5001
```

### Linux/Mac

```bash
# 1. Clone và vào thư mục
cd /path/to/CrabSenseKiosk

# 2. Cấp quyền và setup database
chmod +x setup-database.sh
./setup-database.sh

# 3. Chạy API
dotnet run

# 4. Mở trình duyệt
open https://localhost:5001
```

## 📝 Test API ngay lập tức

### Sử dụng Swagger UI (Khuyến nghị)

Mở: `https://localhost:5001/`

### Sử dụng curl

```bash
# 1. Health Check
curl -k https://localhost:5001/api/health

# 2. Tạo sensor
curl -k -X POST https://localhost:5001/api/sensor \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Test Sensor",
    "sensorType": "Temperature",
    "location": "Pool A"
  }'

# 3. Lấy danh sách sensors
curl -k https://localhost:5001/api/sensor

# 4. Tạo bản đọc chất lượng nước
curl -k -X POST https://localhost:5001/api/waterquality \
  -H "Content-Type: application/json" \
  -d '{
    "temperature": 25.5,
    "pH": 7.8,
    "dissolvedOxygen": 6.5,
    "salinity": 28.0,
    "sensorId": 1
  }'

# 5. Lấy các bản đọc mới nhất
curl -k https://localhost:5001/api/waterquality/latest?count=5

# 6. Lấy thống kê
curl -k https://localhost:5001/api/waterquality/statistics
```

## 🎯 Các endpoints chính

| Endpoint | Method | Mô tả |
|----------|--------|-------|
| `/api/health` | GET | Kiểm tra trạng thái |
| `/api/sensor` | GET | Danh sách sensors |
| `/api/sensor` | POST | Tạo sensor |
| `/api/waterquality` | GET | Danh sách bản đọc |
| `/api/waterquality` | POST | Tạo bản đọc |
| `/api/waterquality/latest` | GET | Bản đọc mới nhất |
| `/api/waterquality/statistics` | GET | Thống kê |
| `/api/alert/active` | GET | Cảnh báo |

## 🔍 Kiểm tra logs

```powershell
# Logs được lưu tại thư mục logs/
Get-Content logs\crabsense-*.txt -Tail 50

# Hoặc
tail -f logs/crabsense-*.txt
```

## ⚙️ Cấu hình nhanh

### Thay đổi port

Mở `appsettings.json` hoặc sử dụng environment variable:

```bash
dotnet run --urls="http://localhost:3000"
```

### Thay đổi connection string

Mở `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=CrabSenseDb;User Id=sa;Password=YourPassword;"
  }
}
```

## 🐛 Xử lý sự cố nhanh

### Lỗi: "Could not execute dotnet-ef"

```bash
dotnet tool install --global dotnet-ef
```

### Lỗi: SQL Server không kết nối được

```bash
# Kiểm tra SQL Server đã chạy
# Windows: Services.msc -> SQL Server

# LocalDB
sqllocaldb start mssqllocaldb
```

### Lỗi: Port đã được sử dụng

```bash
# Sử dụng port khác
dotnet run --urls="https://localhost:6001;http://localhost:6000"
```

### Lỗi: Certificate SSL

```bash
dotnet dev-certs https --trust
```

## 📚 Tài liệu chi tiết

- [SETUP.md](SETUP.md) - Hướng dẫn cài đặt chi tiết
- [README.md](README.md) - Tổng quan về dự án
- [API_DOCUMENTATION.md](API_DOCUMENTATION.md) - Tài liệu API đầy đủ

## 🎓 Ví dụ tích hợp

### JavaScript/Fetch

```javascript
// Lấy danh sách sensors
fetch('https://localhost:5001/api/sensor')
  .then(res => res.json())
  .then(data => console.log(data));

// Tạo bản đọc mới
fetch('https://localhost:5001/api/waterquality', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    temperature: 25.5,
    pH: 7.8,
    dissolvedOxygen: 6.5,
    salinity: 28.0
  })
})
.then(res => res.json())
.then(data => console.log(data));
```

### Python/Requests

```python
import requests

# Lấy danh sách sensors
response = requests.get('https://localhost:5001/api/sensor', verify=False)
print(response.json())

# Tạo bản đọc mới
data = {
    "temperature": 25.5,
    "pH": 7.8,
    "dissolvedOxygen": 6.5,
    "salinity": 28.0
}
response = requests.post(
    'https://localhost:5001/api/waterquality',
    json=data,
    verify=False
)
print(response.json())
```

### C#/HttpClient

```csharp
using System.Net.Http;
using System.Text.Json;

var client = new HttpClient();

// Lấy danh sách sensors
var response = await client.GetAsync("https://localhost:5001/api/sensor");
var json = await response.Content.ReadAsStringAsync();
Console.WriteLine(json);

// Tạo bản đọc mới
var data = new {
    temperature = 25.5,
    pH = 7.8,
    dissolvedOxygen = 6.5,
    salinity = 28.0
};
var content = new StringContent(
    JsonSerializer.Serialize(data),
    System.Text.Encoding.UTF8,
    "application/json"
);
response = await client.PostAsync("https://localhost:5001/api/waterquality", content);
```

## 🚢 Deployment

### Build for Production

```bash
dotnet publish -c Release -o ./publish
```

### Run Production Build

```bash
cd publish
dotnet CrabSenseKiosk.dll
```

### Docker (Optional)

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY ./publish .
ENTRYPOINT ["dotnet", "CrabSenseKiosk.dll"]
```

## ✅ Checklist đầu tiên

- [ ] Cài đặt .NET 8.0 SDK
- [ ] Cài đặt SQL Server
- [ ] Chạy setup-database script
- [ ] Chạy `dotnet run`
- [ ] Mở Swagger UI
- [ ] Test endpoint health
- [ ] Tạo sensor test
- [ ] Tạo bản đọc test
- [ ] Xem logs
- [ ] Đọc API documentation

## 💡 Tips

1. **Sử dụng Swagger UI** - Dễ dàng nhất để test API
2. **Kiểm tra logs** - Mọi thứ đều được log
3. **Xem statistics** - Endpoint hữu ích để giám sát
4. **CORS đã được enable** - Có thể gọi từ bất kỳ origin nào
5. **Validation tự động** - API tự động validate input

## 🤝 Cần trợ giúp?

- Xem logs: `logs/crabsense-*.txt`
- Kiểm tra database: SQL Server Management Studio
- Swagger UI: `https://localhost:5001/`
- Tài liệu API: [API_DOCUMENTATION.md](API_DOCUMENTATION.md)

---

**Chúc bạn code vui vẻ! 🎉**
