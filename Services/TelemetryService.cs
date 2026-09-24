using System.Collections.Concurrent;
using CrabSenseKiosk.DTOs;

namespace CrabSenseKiosk.Services;

/// <summary>
/// Lưu snapshot realtime của ESP32 tại Kiosk.
/// Dữ liệu nghiệp vụ/cloud vẫn do CrabSenseBE đảm nhiệm; Kiosk chỉ giữ trạng thái LAN mới nhất.
/// </summary>
public sealed class TelemetryService : ITelemetryService
{
    private readonly ConcurrentDictionary<string, TelemetrySnapshot> _latest = new(
        StringComparer.OrdinalIgnoreCase);

    public TelemetrySnapshot Store(TelemetryPayload payload)
    {
        var mac = payload.Mac!.Trim();
        var snapshot = new TelemetrySnapshot(
            mac,
            DateTime.UtcNow,
            payload.Readings
                .Select(reading => new TelemetryReadingDto
                {
                    Pin = reading.Pin,
                    Value = reading.Value
                })
                .ToArray());

        _latest[mac] = snapshot;
        return snapshot;
    }

    public TelemetrySnapshot? GetLatest(string? mac = null)
    {
        if (!string.IsNullOrWhiteSpace(mac))
            return _latest.TryGetValue(mac.Trim(), out var snapshot) ? snapshot : null;

        return _latest.Values
            .OrderByDescending(snapshot => snapshot.ReceivedAt)
            .FirstOrDefault();
    }
}
