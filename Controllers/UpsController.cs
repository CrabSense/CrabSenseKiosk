using CrabSenseKiosk.DTOs;
using CrabSenseKiosk.Services;
using Microsoft.AspNetCore.Mvc;

namespace CrabSenseKiosk.Controllers;

/// <summary>
/// UPS (CyberPower USB HID) cắm trực tiếp vào máy Kiosk — KHÁC với ESP32/ACD301.
/// Vì vậy đường này bỏ qua ControllerRegistry (không có MAC/IP/đăng ký controller)
/// nhưng vẫn đẩy vào TelemetryOutbox để được retry và gửi BE như mọi telemetry.
/// </summary>
[ApiController]
public sealed class UpsController : ControllerBase
{
    private readonly UpsTelemetryService _ups;
    private readonly TelemetryOutbox _outbox;
    private readonly ILogger<UpsController> _logger;

    public UpsController(
        UpsTelemetryService ups,
        TelemetryOutbox outbox,
        ILogger<UpsController> logger)
    {
        _ups = ups;
        _outbox = outbox;
        _logger = logger;
    }

    [HttpPost("/api/ups/telemetry")]
    public async Task<ActionResult<object>> Ingest(
        [FromBody] UpsTelemetryPayload payload,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payload.DeviceCode))
            return BadRequest(new { ok = false, error = "deviceCode is required" });

        if (payload.Readings.Count == 0)
            return BadRequest(new { ok = false, error = "readings is required" });

        if (payload.Readings.Any(r =>
                string.IsNullOrWhiteSpace(r.SensorCode) || !double.IsFinite(r.Value)))
            return BadRequest(new { ok = false, error = "readings contains an invalid sensor or value" });

        var deviceCode = payload.DeviceCode.Trim();
        var snapshot = new TelemetrySnapshot(
            deviceCode,
            deviceCode,   // UPS không có MAC — dùng deviceCode làm khoá
            null,         // không có IP LAN
            payload.MeasuredAt?.ToUniversalTime() ?? DateTime.UtcNow,
            payload.Readings.Select((r, index) => new TelemetryReadingDto
            {
                Pin = index + 1,
                SensorCode = r.SensorCode!.Trim(),
                Value = r.Value,
                Unit = string.IsNullOrWhiteSpace(r.Unit) ? null : r.Unit.Trim()
            }).ToArray());

        _ups.Store(payload);
        await _outbox.EnqueueAsync(snapshot, ct);

        _logger.LogInformation(
            "Received {ReadingCount} UPS readings from {DeviceCode}",
            snapshot.Readings.Count,
            deviceCode);

        return StatusCode(StatusCodes.Status201Created, new
        {
            ok = true,
            deviceCode,
            receivedAt = snapshot.ReceivedAt,
            readingCount = snapshot.Readings.Count
        });
    }

    [HttpGet("/api/ups/latest")]
    public ActionResult<object> Latest([FromQuery] string? deviceCode = null)
    {
        var snapshot = _ups.GetLatest(deviceCode);
        return snapshot is null
            ? NotFound(new { ok = false, error = "No UPS telemetry received" })
            : Ok(new { ok = true, data = snapshot });
    }

    [HttpGet("/api/ups/snapshots")]
    public ActionResult<object> Snapshots() =>
        Ok(new { ok = true, data = _ups.GetAll() });
}
