# CrabSenseKiosk

> Ứng dụng **Desktop / Kiosk / Wallboard** — màn hình lớn treo tường và bàn thu ngân PC.

Repo này triển khai các màn **Platform = Desktop** trong sheet `02_Man_hinh_UI` (Member 2 — Desktop layout).

## Vai trò

| Mục | Nội dung (Excel) |
|-----|------------------|
| Mục tiêu | Giám sát realtime, console vận hành, thu ngân, báo cáo rộng |
| FR | FR-9 dashboard/reports, FR-2 WQ wall view, FR-10 sales PC |
| Layout | Fullscreen multi-panel; ít form dày; auto-refresh |

## Màn hình Desktop (Excel)

| Mã MH | Route | Ý nghĩa |
|-------|-------|---------|
| DESK-Dashboard | `/desk/wallboard` | Wallboard: KPI lớn + WQ mosaic + alert ticker + AI harvest list |
| DESK-RAS-Map | `/desk/ras-map` | Canvas sơ đồ RAS / zone / sensor status |
| DESK-Ops-Console | `/desk/ops` | 3 cột: alert + video due + AI reco; phím tắt; âm thanh critical |
| DESK-Sales | `/desk/sales` | Thu ngân PC: customer + cart + payment + in hóa đơn |
| DESK-Reports | `/desk/reports` | Hub export Excel/PDF các báo cáo |
| DESK-Admin | `/desk/admin` | Users / thresholds / farm / Notification Center |
| DESK-FlutterWin | `ras_desktop` | Optional: reuse Flutter mobile layouts phóng to (P2) |

## Nguyên tắc UI (Excel)

- Không nhồi form CRUD phức tạp trên wallboard — click drill-down sang Web FE nếu cần.
- Auto-refresh dashboard / WQ / alerts.
- Sales kiosk: shortcut keys, layout rộng, in invoice + QR.

## Stack đề xuất

- React (có thể share package với CrabSenseFE) chạy kiosk mode / fullscreen
- hoặc Electron / PWA cài máy tính trại
- Optional: Flutter Windows (`ras_desktop`)

## Liên kết

- Web admin đầy đủ: [CrabSenseFE](https://github.com/CrabSense/CrabSenseFE)
- API: [CrabSenseBE](https://github.com/CrabSense/CrabSenseBE)
