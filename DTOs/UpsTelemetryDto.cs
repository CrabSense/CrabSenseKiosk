using System.Text.Json.Serialization;

namespace CrabSenseKiosk.DTOs;

/// <summary>
/// Payload UPS (USB HID, cắm trực tiếp vào máy Kiosk) gửi vào Kiosk.
/// Khác TelemetryPayload của ESP32: không có mac / pin, định danh bằng deviceCode.
/// </summary>
public sealed class UpsTelemetryPayload
{
    [JsonPropertyName("deviceCode")]
    public string? DeviceCode { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("manufacturer")]
    public string? Manufacturer { get; set; }

    [JsonPropertyName("serial")]
    public string? Serial { get; set; }

    [JsonPropertyName("measuredAt")]
    public DateTime? MeasuredAt { get; set; }

    [JsonPropertyName("readings")]
    public List<UpsReadingDto> Readings { get; set; } = [];
}

public sealed class UpsReadingDto
{
    [JsonPropertyName("sensor")]
    public string? SensorCode { get; set; }

    [JsonPropertyName("val")]
    public double Value { get; set; }

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }
}

public sealed record UpsSnapshot(
    string DeviceCode,
    string? Model,
    string? Manufacturer,
    string? Serial,
    DateTime ReceivedAt,
    IReadOnlyList<UpsReadingDto> Readings);
