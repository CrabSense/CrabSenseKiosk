using System.Text.Json.Serialization;

namespace CrabSenseKiosk.DTOs;

/// <summary>
/// Payload do ESP32 gửi vào Kiosk trong mạng LAN.
/// Giữ cùng contract với gateway hiện có: mac + readings[{ pin, val }].
/// </summary>
public sealed class TelemetryPayload
{
    [JsonPropertyName("mac")]
    public string? Mac { get; set; }

    [JsonPropertyName("readings")]
    public List<TelemetryReadingDto> Readings { get; set; } = [];
}

public sealed class TelemetryReadingDto
{
    [JsonPropertyName("pin")]
    public int Pin { get; set; }

    [JsonPropertyName("val")]
    public double Value { get; set; }
}

public sealed record TelemetrySnapshot(
    string Mac,
    DateTime ReceivedAt,
    IReadOnlyList<TelemetryReadingDto> Readings);
