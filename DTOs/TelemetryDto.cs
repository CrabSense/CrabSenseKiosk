using System.Text.Json.Serialization;

namespace CrabSenseKiosk.DTOs;

/// <summary>
/// Payload do ESP32 gửi vào Kiosk trong mạng LAN.
/// Giữ cùng contract với gateway hiện có: mac + readings[{ pin, val }].
/// </summary>
public sealed class TelemetryPayload
{
    [JsonPropertyName("deviceCode")]
    public string? DeviceCode { get; set; }

    [JsonPropertyName("mac")]
    public string? Mac { get; set; }

    [JsonPropertyName("ipAddress")]
    public string? IpAddress { get; set; }

    [JsonPropertyName("readings")]
    public List<TelemetryReadingDto> Readings { get; set; } = [];
}

public sealed class TelemetryReadingDto
{
    [JsonPropertyName("pin")]
    public int Pin { get; set; }

    [JsonPropertyName("sensor")]
    public string? SensorCode { get; set; }

    [JsonPropertyName("val")]
    public double Value { get; set; }
}

public sealed record TelemetrySnapshot(
    string DeviceCode,
    string Mac,
    string? IpAddress,
    DateTime ReceivedAt,
    IReadOnlyList<TelemetryReadingDto> Readings);
