# CrabSense Kiosk API Documentation

Tài liệu chi tiết về các API endpoints của hệ thống CrabSense Kiosk.

## Base URL

```
Development: https://localhost:5001/api
Production: https://your-domain.com/api
```

## Authentication

Hiện tại API chưa yêu cầu authentication. Sẽ được thêm vào các phiên bản sau với JWT Bearer Token.

---

## 1. Health Check API

### 1.1. Kiểm tra trạng thái API

**Endpoint:** `GET /health`

**Response:**
```json
{
  "success": true,
  "message": "API đang hoạt động bình thường",
  "data": {
    "status": "Healthy",
    "timestamp": "2024-07-18T10:30:00Z",
    "version": "1.0.0",
    "service": "CrabSense Kiosk API"
  }
}
```

### 1.2. Kiểm tra chi tiết hệ thống

**Endpoint:** `GET /health/detailed`

**Response:**
```json
{
  "success": true,
  "data": {
    "status": "Healthy",
    "timestamp": "2024-07-18T10:30:00Z",
    "version": "1.0.0",
    "service": "CrabSense Kiosk API",
    "environment": "Development",
    "machineName": "KIOSK-001",
    "osVersion": "Microsoft Windows 10.0.19045",
    "processorCount": 8,
    "systemUptime": "2.15:30:45"
  }
}
```

---

## 2. Sensor API

### 2.1. Tạo sensor mới

**Endpoint:** `POST /sensor`

**Request Body:**
```json
{
  "name": "Sensor 1 - Pool A",
  "sensorType": "Temperature",
  "location": "Pool A - North Corner",
  "isActive": true,
  "description": "Main temperature sensor for Pool A"
}
```

**Response:**
```json
{
  "success": true,
  "message": "Tạo sensor thành công",
  "data": {
    "id": 1,
    "name": "Sensor 1 - Pool A",
    "sensorType": "Temperature",
    "location": "Pool A - North Corner",
    "isActive": true,
    "installationDate": "2024-07-18T10:30:00Z",
    "description": "Main temperature sensor for Pool A"
  }
}
```

### 2.2. Lấy thông tin sensor

**Endpoint:** `GET /sensor/{id}`

**Response:**
```json
{
  "success": true,
  "data": {
    "id": 1,
    "name": "Sensor 1 - Pool A",
    "sensorType": "Temperature",
    "location": "Pool A - North Corner",
    "isActive": true,
    "installationDate": "2024-07-18T10:30:00Z",
    "lastMaintenanceDate": "2024-07-15T08:00:00Z",
    "nextMaintenanceDate": "2024-08-15T08:00:00Z",
    "description": "Main temperature sensor for Pool A"
  }
}
```

### 2.3. Lấy danh sách sensors

**Endpoint:** `GET /sensor`

**Response:**
```json
{
  "success": true,
  "data": [
    {
      "id": 1,
      "name": "Sensor 1 - Pool A",
      "sensorType": "Temperature",
      "location": "Pool A - North Corner",
      "isActive": true,
      "installationDate": "2024-07-18T10:30:00Z"
    }
  ]
}
```

### 2.4. Lấy sensors đang hoạt động

**Endpoint:** `GET /sensor/active`

### 2.5. Cập nhật sensor

**Endpoint:** `PUT /sensor/{id}`

**Request Body:**
```json
{
  "name": "Sensor 1 - Pool A Updated",
  "sensorType": "Temperature",
  "location": "Pool A - North Corner",
  "isActive": true,
  "lastMaintenanceDate": "2024-07-18T08:00:00Z",
  "nextMaintenanceDate": "2024-08-18T08:00:00Z",
  "description": "Updated description"
}
```

### 2.6. Xóa sensor

**Endpoint:** `DELETE /sensor/{id}`

### 2.7. Bật/tắt sensor

**Endpoint:** `PATCH /sensor/{id}/toggle`

---

## 3. Water Quality API

### 3.1. Tạo bản đọc chất lượng nước

**Endpoint:** `POST /waterquality`

**Request Body:**
```json
{
  "temperature": 25.5,
  "pH": 7.8,
  "dissolvedOxygen": 6.5,
  "salinity": 28.0,
  "turbidity": 15.3,
  "ammonia": 0.5,
  "sensorId": 1,
  "notes": "Normal reading"
}
```

**Response:**
```json
{
  "success": true,
  "message": "Tạo bản đọc thành công",
  "data": {
    "id": 1,
    "timestamp": "2024-07-18T10:30:00Z",
    "temperature": 25.5,
    "pH": 7.8,
    "dissolvedOxygen": 6.5,
    "salinity": 28.0,
    "turbidity": 15.3,
    "ammonia": 0.5,
    "sensorId": 1,
    "sensorName": "Sensor 1 - Pool A",
    "notes": "Normal reading",
    "isAnomaly": false
  }
}
```

### 3.2. Lấy thông tin bản đọc

**Endpoint:** `GET /waterquality/{id}`

### 3.3. Lấy danh sách bản đọc

**Endpoint:** `GET /waterquality`

**Query Parameters:**
- `startDate` (DateTime): Ngày bắt đầu
- `endDate` (DateTime): Ngày kết thúc
- `sensorId` (int): ID sensor
- `onlyAnomalies` (bool): Chỉ lấy bất thường
- `pageNumber` (int): Số trang (default: 1)
- `pageSize` (int): Số bản ghi/trang (default: 20)

**Response:**
```json
{
  "success": true,
  "data": {
    "data": [
      {
        "id": 1,
        "timestamp": "2024-07-18T10:30:00Z",
        "temperature": 25.5,
        "pH": 7.8,
        "dissolvedOxygen": 6.5,
        "salinity": 28.0,
        "isAnomaly": false
      }
    ],
    "pageNumber": 1,
    "pageSize": 20,
    "totalRecords": 100,
    "totalPages": 5,
    "hasPreviousPage": false,
    "hasNextPage": true
  }
}
```

### 3.4. Lấy các bản đọc mới nhất

**Endpoint:** `GET /waterquality/latest?count=10`

### 3.5. Lấy thống kê chất lượng nước

**Endpoint:** `GET /waterquality/statistics`

**Query Parameters:**
- `startDate` (DateTime): Ngày bắt đầu
- `endDate` (DateTime): Ngày kết thúc
- `sensorId` (int): ID sensor

**Response:**
```json
{
  "success": true,
  "data": {
    "startDate": "2024-07-11T00:00:00Z",
    "endDate": "2024-07-18T23:59:59Z",
    "totalReadings": 1000,
    "anomalyCount": 15,
    "anomalyPercentage": 1.5,
    "temperature": {
      "min": 22.0,
      "max": 28.5,
      "average": 25.3,
      "current": 25.5,
      "status": "Bình thường"
    },
    "pH": {
      "min": 7.2,
      "max": 8.3,
      "average": 7.75,
      "current": 7.8,
      "status": "Bình thường"
    },
    "dissolvedOxygen": {
      "min": 5.2,
      "max": 9.8,
      "average": 7.5,
      "current": 6.5,
      "status": "Bình thường"
    },
    "salinity": {
      "min": 26.0,
      "max": 32.0,
      "average": 28.5,
      "current": 28.0,
      "status": "Bình thường"
    }
  }
}
```

### 3.6. Xóa bản đọc

**Endpoint:** `DELETE /waterquality/{id}`

---

## 4. Alert API

### 4.1. Lấy cảnh báo đang hoạt động

**Endpoint:** `GET /alert/active`

**Response:**
```json
{
  "success": true,
  "data": [
    "18/07/2024 10:30 - Sensor 1 - Pool A: Phát hiện bất thường",
    "18/07/2024 09:15 - Sensor 2 - Pool B: Phát hiện bất thường"
  ]
}
```

---

## Status Codes

- `200 OK` - Thành công
- `201 Created` - Tạo mới thành công
- `400 Bad Request` - Dữ liệu không hợp lệ
- `404 Not Found` - Không tìm thấy
- `500 Internal Server Error` - Lỗi server

---

## Error Response Format

```json
{
  "success": false,
  "message": "Mô tả lỗi",
  "errors": [
    "Chi tiết lỗi 1",
    "Chi tiết lỗi 2"
  ]
}
```

---

## Validation Rules

### Water Quality Readings

- Temperature: -10 đến 50°C
- pH: 0 đến 14
- Dissolved Oxygen: 0 đến 20 mg/L
- Salinity: 0 đến 50 ppt
- Turbidity: 0 đến 1000 NTU
- Ammonia: 0 đến 10 mg/L

### Sensors

- Name: Tối đa 100 ký tự
- SensorType: Tối đa 50 ký tự
- Location: Tối đa 200 ký tự
- Description: Tối đa 500 ký tự

---

## Rate Limiting

Hiện tại chưa có rate limiting. Sẽ được thêm vào các phiên bản sau.

---

## Changelog

### Version 1.0.0 (2024-07-18)
- Initial release
- Basic CRUD operations for Sensors
- Water Quality Readings management
- Alert system
- Statistics API
