using CrabSenseKiosk.DTOs;
using CrabSenseKiosk.Services;
using Microsoft.AspNetCore.Mvc;

namespace CrabSenseKiosk.Controllers;

[ApiController]
public sealed class TelemetryController : ControllerBase
{
    private readonly ITelemetryService _telemetry;
    private readonly ILogger<TelemetryController> _logger;

    public TelemetryController(
        ITelemetryService telemetry,
        ILogger<TelemetryController> logger)
    {
        _telemetry = telemetry;
        _logger = logger;
    }

    /// <summary>ESP32 gửi dữ liệu realtime vào Kiosk qua mạng LAN.</summary>
    [HttpPost("/telemetry")]
    [HttpPost("/api/telemetry")]
    public ActionResult<object> Ingest([FromBody] TelemetryPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Mac))
            return BadRequest(new { ok = false, error = "mac is required" });

        if (payload.Readings.Count == 0)
            return BadRequest(new { ok = false, error = "readings is required" });

        if (payload.Readings.Any(reading => reading.Pin <= 0 || !double.IsFinite(reading.Value)))
            return BadRequest(new { ok = false, error = "readings contains an invalid pin or value" });

        var snapshot = _telemetry.Store(payload);
        _logger.LogInformation(
            "Received {ReadingCount} readings from ESP32 {Mac}",
            snapshot.Readings.Count,
            snapshot.Mac);

        return StatusCode(StatusCodes.Status201Created, new
        {
            ok = true,
            mac = snapshot.Mac,
            receivedAt = snapshot.ReceivedAt,
            readingCount = snapshot.Readings.Count
        });
    }

    /// <summary>Lấy snapshot mới nhất của một ESP32 hoặc toàn bộ Kiosk.</summary>
    [HttpGet("/api/telemetry/latest")]
    public ActionResult<object> Latest([FromQuery] string? mac = null)
    {
        var snapshot = _telemetry.GetLatest(mac);
        return snapshot is null
            ? NotFound(new { ok = false, error = "No telemetry received" })
            : Ok(new { ok = true, data = snapshot });
    }
}
