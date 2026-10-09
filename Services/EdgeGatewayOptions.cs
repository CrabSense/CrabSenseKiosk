namespace CrabSenseKiosk.Services;

public sealed class EdgeGatewayOptions
{
    public const string SectionName = "EdgeGateway";

    public string BackendUrl { get; set; } = "http://localhost:5080";
    public string ApiKey { get; set; } = "crabsense-edge-dev-key";
    public string? KioskSecret { get; set; }
    public int PollIntervalSeconds { get; set; } = 2;
    public int TelemetryForwardIntervalSeconds { get; set; } = 2;
}
