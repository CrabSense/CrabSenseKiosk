# CrabSenseKiosk

> **Edge gateway / PC trại** — nhận dữ liệu từ **IoT** và **AI Water Quality**, đóng gói **HDF5**, đồng bộ lên **CrabSenseBE** (cloud).

Đây **không** phải màn wallboard/thu ngân Web. UI treo tường / desktop sales nằm ở [CrabSenseFE](https://github.com/CrabSense/CrabSenseFE) (fullscreen) hoặc app riêng.  
**Kiosk = runtime biên (offline-first)** theo kiến trúc Edge → Cloud.

## Vai trò trong hệ thống

```
ESP32 / sensors          AI WQ service
   (CrabSenseIot)     (CrabSenseAIWaterQualityAnalysis)
           \               /
            \             /
             v           v
         ┌─────────────────────┐
         │   CrabSenseKiosk    │  ← máy tính / mini-PC tại trại
         │  - ingest realtime  │
         │  - buffer local     │
         │  - đóng gói HDF5    │
         │  - sync queue       │
         └──────────┬──────────┘
                    │  POST /api/sync/hdf5  (+ telemetry hot path)
                    v
              CrabSenseBE (+ MinIO/S3 index)
```

| Nguồn | Kiosk làm gì |
|-------|----------------|
| **CrabSenseIot** | Nhận telemetry (temp, pH, DO, salinity, water_level), device status (battery, RSSI, firmware) |
| **CrabSenseAIWaterQualityAnalysis** | Nhận kết quả phân tích / anomaly / snapshot WQ (và có thể gắn kèm detection metadata nếu pipeline chạy local) |
| **Local disk** | Ghi buffer nóng (optional Influx/SQLite) + **cold archive HDF5** |
| **CrabSenseBE** | Upload file HDF5 đã seal + metadata; đẩy sample realtime khi online |

## Trách nhiệm chính

1. **Ingest** — HTTP/MQTT/LAN từ IoT nodes và AI service trên mạng trại  
2. **Normalize** — chuẩn hóa theo `device_code` / `farm_id` / pin metric  
3. **Hot path** (tuỳ chọn) — giữ vài phút–vài giờ gần nhất để dashboard local / AI local đọc nhanh  
4. **Cold path — HDF5** — append theo chunk (giờ / N phút / max size), seal file  
5. **Sync** — hàng đợi upload lên BE khi có mạng; retry idempotent  
6. **Health** — online status thiết bị, dung lượng ổ, trạng thái sync queue  

## Chuẩn HDF5 (gợi ý)

```
/data/hdf5/
  {device_code}_{YYYYMMDD}_{HH}.h5   # hoặc rotate theo chunk
```

Cloud (sau khi sync):

```
s3://…/{farm_id}/{device_code}/{epoch}_{filename}.h5
hdf5_uploads → farm_id, device_code, chunk_start/end, storage_path, checksum
```

Idempotency key ví dụ: `farm_id|device_code|chunk_start_ms|checksum` — tránh trùng khi retry.

## API phía BE cần hỗ trợ

- `POST /api/sync/hdf5` — upload file + metadata (API key / device credential)
- `POST /api/iot/sensor-data` — đường nóng realtime (nếu không chỉ dựa HDF5)
- `GET/PUT …/devices/{code}/shadow` — desired/reported config (optional)
- Index: bảng `hdf5_uploads` (metadata), object storage MinIO/S3

## Stack đề xuất

- Python hoặc .NET worker trên Windows/Linux mini-PC tại trại  
- `h5py` (hoặc lib HDF5 tương đương)  
- Local queue + retry (SQLite / files)  
- Docker Compose edge: ingest API + sync job + volume `/data/hdf5`

## Nguyên tắc thiết kế

- **Offline-first** — mất mạng vẫn ghi HDF5 local; lên mạng mới sync  
- **IoT không nói chuyện thẳng cloud** (mặc định) — đi qua Kiosk  
- **AI WQ** có thể chạy cạnh Kiosk; kết quả được Kiosk gắn vào archive / đẩy BE  
- Tách rõ: **Influx/hot = nóng**, **HDF5 = lạnh**, **Postgres BE = metadata nghiệp vụ**

## Liên kết repo

| Repo | Quan hệ với Kiosk |
|------|-------------------|
| [CrabSenseIot](https://github.com/CrabSense/CrabSenseIot) | Nguồn telemetry / device |
| [CrabSenseAIWaterQualityAnalysis](https://github.com/CrabSense/CrabSenseAIWaterQualityAnalysis) | Nguồn phân tích WQ / AI local |
| [CrabSenseBE](https://github.com/CrabSense/CrabSenseBE) | Đích sync HDF5 + API nghiệp vụ |
| [CrabSenseFE](https://github.com/CrabSense/CrabSenseFE) | Xem archive / realtime sau khi BE nhận |
| [CrabSenseApp](https://github.com/CrabSense/CrabSenseApp) | Mobile hiện trường (không thay Kiosk) |

## Ghi chú Excel

Sheet `02_Man_hinh_UI` Platform=**Desktop** (wallboard / thu ngân) → implement trên **CrabSenseFE** (hoặc PWA fullscreen), **không** nhầm với repo Kiosk edge này.
