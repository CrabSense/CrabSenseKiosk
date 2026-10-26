using System.Collections.Concurrent;
using CrabSenseKiosk.DTOs;

namespace CrabSenseKiosk.Services;

/// <summary>
/// Giữ snapshot UPS mới nhất tại Kiosk (chỉ để xem/debug).
/// Đẩy lên BE vẫn đi qua TelemetryOutbox như mọi telemetry khác.
/// </summary>
public sealed class UpsTelemetryService
{
    private readonly ConcurrentDictionary<string, UpsSnapshot> _latest = new(
        StringComparer.OrdinalIgnoreCase);

    public UpsSnapshot Store(UpsTelemetryPayload payload)
    {
        var snapshot = new UpsSnapshot(
            payload.DeviceCode!.Trim(),
            payload.Model,
            payload.Manufacturer,
            payload.Serial,
            payload.MeasuredAt?.ToUniversalTime() ?? DateTime.UtcNow,
            payload.Readings.ToArray());

        _latest[snapshot.DeviceCode] = snapshot;
        return snapshot;
    }

    public UpsSnapshot? GetLatest(string? deviceCode = null)
    {
        if (!string.IsNullOrWhiteSpace(deviceCode))
            return _latest.TryGetValue(deviceCode.Trim(), out var hit) ? hit : null;

        return _latest.Values.OrderByDescending(x => x.ReceivedAt).FirstOrDefault();
    }

    public IReadOnlyList<UpsSnapshot> GetAll() =>
        _latest.Values.OrderByDescending(x => x.ReceivedAt).ToArray();
}
