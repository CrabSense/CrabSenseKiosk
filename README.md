# CrabSenseKiosk - API RESTful

> **Edge gateway lắp tại trại nuôi** — nhận data từ **IoT** + **AI Water Quality**, đóng gói **HDF5**, sync lên **CrabSenseBE**.

## 🚀 API Documentation

API RESTful được xây dựng bằng **ASP.NET Core 8.0** với các tính năng:

- ✅ Quản lý cảm biến (Sensors)
- ✅ Ghi nhận và phân tích chất lượng nước
- ✅ Phát hiện bất thường tự động
- ✅ Hệ thống cảnh báo thông minh
- ✅ Thống kê và báo cáo
- ✅ Swagger UI Documentation
- ✅ Logging với Serilog

### Chạy API

```bash
dotnet run
```

API: `https://localhost:5001` | Swagger UI: `https://localhost:5001/`

### API Endpoints

**Health Check:**
- `GET /api/health` - Kiểm tra trạng thái API
- `GET /api/health/detailed` - Chi tiết hệ thống

**Water Quality:**
- `POST /api/waterquality` - Tạo bản đọc mới
- `GET /api/waterquality/{id}` - Lấy bản đọc theo ID
- `GET /api/waterquality` - Danh sách (phân trang)
- `GET /api/waterquality/latest` - Các bản đọc mới nhất
- `GET /api/waterquality/statistics` - Thống kê

**Sensors:**
- `POST /api/sensor` - Tạo sensor
- `GET /api/sensor` - Danh sách sensors
- `GET /api/sensor/active` - Sensors hoạt động
- `PUT /api/sensor/{id}` - Cập nhật
- `PATCH /api/sensor/{id}/toggle` - Bật/tắt

**Alerts:**
- `GET /api/alert/active` - Cảnh báo đang hoạt động

**ESP32 LAN telemetry:**
- `POST /telemetry` hoặc `POST /api/telemetry` - ESP32 gửi `{ deviceCode, mac, ipAddress, readings: [{ pin, val }] }`
- `GET /api/telemetry/latest?mac=...` - Đọc snapshot mới nhất tại Kiosk
- `GET /api/telemetry/snapshots` - Danh sách snapshot thiết bị tại Kiosk

Chạy Kiosk cho ESP32 trong cùng mạng LAN:

```bash
dotnet run --urls "http://0.0.0.0:8090"
```

ESP32 gửi tới `http://<IP-MAY-KIOSK>:8090/telemetry`.
Cloud `CrabSenseBE` vẫn giữ nguyên các endpoint `/api/iot/sensor-data`;
Kiosk chỉ là điểm nhận local, không thay thế hay xóa API cloud.

Kiosk polls BE Cloud for commands every 2 seconds, forwards them to the
reported ESP IP at `POST /api/command`, then acknowledges the result at BE.
Configure `EdgeGateway:BackendUrl` and `EdgeGateway:ApiKey` in
`appsettings.json`.

## Phạm vi (quan trọng)

| Có trên Kiosk | Không có trên Kiosk |
|---------------|---------------------|
| Ingest telemetry từ CrabSenseIot | Thanh toán / hóa đơn / công nợ |
| Nhận kết quả từ CrabSenseAIWaterQualityAnalysis | CRM khách hàng / bảng giá / sales order |
| Buffer local + đóng gói HDF5 | Wallboard UI / thu ngân / báo cáo bán hàng |
| Hàng đợi sync → BE khi có mạng | CRUD nuôi cua, harvest voucher UI |

**Thanh toán & hóa đơn** thuộc **CrabSenseBE** (API) + **CrabSenseFE** (UI văn phòng / sales).  
Kiosk chỉ là máy biên tại ao — không làm POS.

## Luồng dữ liệu

```
CrabSenseIot (ESP32/sensors)
        \
         \     CrabSenseAIWaterQualityAnalysis
          \             /
           v           v
      ┌──────────────────────┐
      │  CrabSenseKiosk      │  mini-PC / gateway tại trại
      │  ingest + commands   │
      │  HDF5 → sync         │
      └──────────┬───────────┘
                 │  POST /api/sync/hdf5
                 │  (+ optional hot telemetry)
                 v
           CrabSenseBE (cloud)
                 │
                 ├── CrabSenseFE  ← monitoring + **bán hàng / hóa đơn**
                 └── CrabSenseApp ← mobile hiện trường
```

## Trách nhiệm

1. Nhận telemetry / device status từ IoT (LAN)
2. Poll command từ BE và chuyển xuống ESP qua LAN
3. ACK kết quả command về BE
4. Nhận snapshot / kết quả phân tích WQ từ AI service cạnh trại
5. Chuẩn hóa theo `farm_id` / `device_code`
6. Ghi cold archive **HDF5** (rotate theo giờ/chunk)
7. Upload idempotent lên BE (+ MinIO/S3 index)
8. Health: online thiết bị, dung lượng ổ, trạng thái sync queue

## Không thuộc repo này

- `payment`, `invoice`, `customer`, `price_list`, `sales_order`, `delivery` → **BE + FE**  
- Desktop wallboard / thu ngân (Excel `Platform=Desktop`) → **CrabSenseFE** fullscreen  
- Bán nhanh softshell tại ao (nếu cần) → **CrabSenseApp** (`MOB-QuickSale`), không phải Kiosk gateway  

## API phía BE

- `POST /api/sync/hdf5` — file HDF5 + metadata  
- `POST /api/iot/sensor-data` — hot path (tuỳ chọn)  
- Device shadow / API key thiết bị (tuỳ chọn)  

## Stack đề xuất

- Worker Python/.NET trên mini-PC trại  
- `h5py` + volume `/data/hdf5`  
- Offline-first queue + retry  

## Liên kết

| Repo | Vai trò |
|------|---------|
| [CrabSenseIot](https://github.com/CrabSense/CrabSenseIot) | Nguồn sensor |
| [CrabSenseAIWaterQualityAnalysis](https://github.com/CrabSense/CrabSenseAIWaterQualityAnalysis) | Nguồn phân tích WQ |
| [CrabSenseBE](https://github.com/CrabSense/CrabSenseBE) | Nhận HDF5 + API nghiệp vụ (gồm payment/invoice) |
| [CrabSenseFE](https://github.com/CrabSense/CrabSenseFE) | UI bán hàng / hóa đơn / giám sát |
| [CrabSenseApp](https://github.com/CrabSense/CrabSenseApp) | Mobile hiện trường |

## Water quality V2 UART integration
Opt-in separate WaterBench worker and local supervised API, bounded evidence spool and JPEG verification. See [P2.1 water bench](docs/water-bench-p2.1.md). No cloud upload or unattended operation.
