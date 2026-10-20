namespace CrabSenseKiosk.Services;

public sealed class EdgeGatewayOptions
{
    public const string SectionName = "EdgeGateway";

    public string BackendUrl { get; set; } = string.Empty;
    /// <summary>X-Edge-Key — phải cấu hình qua env (EdgeGateway__ApiKey), không mặc định trong code.</summary>
    public string ApiKey { get; set; } = string.Empty;
    public string? KioskSecret { get; set; }
    public int PollIntervalSeconds { get; set; } = 2;
    public int TelemetryForwardIntervalSeconds { get; set; } = 2;
}
