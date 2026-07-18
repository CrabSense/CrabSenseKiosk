# CrabSenseKiosk

> **Edge gateway lắp tại trại nuôi** — nhận data từ **IoT** + **AI Water Quality**, đóng gói **HDF5**, sync lên **CrabSenseBE**.

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
      │  ingest → HDF5 → sync│
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
2. Nhận snapshot / kết quả phân tích WQ từ AI service cạnh trại  
3. Chuẩn hóa theo `farm_id` / `device_code`  
4. Ghi cold archive **HDF5** (rotate theo giờ/chunk)  
5. Upload idempotent lên BE (+ MinIO/S3 index)  
6. Health: online thiết bị, dung lượng ổ, trạng thái sync queue  

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
