using CrabSenseKiosk.DTOs;

namespace CrabSenseKiosk.Services;

public interface ITelemetryService
{
    TelemetrySnapshot Store(TelemetryPayload payload);
    TelemetrySnapshot? GetLatest(string? mac = null);
    IReadOnlyList<TelemetrySnapshot> GetAll();
}
