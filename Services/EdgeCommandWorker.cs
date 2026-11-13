using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CrabSenseKiosk.Services;

public sealed class EdgeCommandWorker : BackgroundService
{
    private readonly ITelemetryService _telemetry;
    private readonly IHttpClientFactory _httpClients;
    private readonly EdgeGatewayOptions _options;
    private readonly ILogger<EdgeCommandWorker> _logger;
    private readonly Dictionary<string, DateTime> _lastForwarded = new(
        StringComparer.OrdinalIgnoreCase);

    private readonly IControllerRegistry _controllers;

    public EdgeCommandWorker(
        ITelemetryService telemetry,
        IHttpClientFactory httpClients,
        IOptions<EdgeGatewayOptions> options,
        IControllerRegistry controllers,
        ILogger<EdgeCommandWorker> logger)
    {
        _telemetry = telemetry;
        _httpClients = httpClients;
        _options = options.Value;
        _controllers = controllers;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Edge poll skipped: {Message}", ex.Message);
            }

            await Task.Delay(
                TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds)),
                stoppingToken);
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var backend = _options.BackendUrl.Trim().TrimEnd('/');
        if (!backend.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !backend.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Skipping backend because the URL is invalid: {Backend}", backend);
            return;
        }

        foreach (var snapshot in _telemetry.GetAll())
        {
            if (string.IsNullOrWhiteSpace(snapshot.IpAddress))
                continue;
            if (!_controllers.IsAccepted(snapshot.DeviceCode))
                continue;
            if (_controllers.IsBlocked(snapshot.DeviceCode))
            {
                _logger.LogWarning("Skip telemetry for rejected controller {DeviceCode}", snapshot.DeviceCode);
                continue;
            }

            try
            {
                var client = _httpClients.CreateClient();
                await ForwardTelemetryAsync(snapshot, backend, client, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(
                    "Edge command poll unavailable for {DeviceCode}: {Message}",
                    snapshot.DeviceCode,
                    ex.Message);
            }
        }
    }

    private async Task ForwardTelemetryAsync(
        DTOs.TelemetrySnapshot snapshot,
        string backend,
        HttpClient client,
        CancellationToken ct)
    {
        if (_lastForwarded.TryGetValue(snapshot.DeviceCode, out var last)
            && DateTime.UtcNow - last
                < TimeSpan.FromSeconds(_options.TelemetryForwardIntervalSeconds))
            return;

        var measurements = snapshot.Readings.Select(reading => new
        {
            deviceCode = snapshot.DeviceCode,
            sensorCode = string.IsNullOrWhiteSpace(reading.SensorCode)
                ? $"float_{reading.Pin}"
                : reading.SensorCode,
            value = reading.Value,
            unit = string.IsNullOrWhiteSpace(reading.Unit) ? "state" : reading.Unit,
            measuredAt = snapshot.ReceivedAt,
            ipAddress = snapshot.IpAddress
        }).ToList();

        // meter_min and meter_c are new codes. The live API rejects an unknown
        // code and fails the whole batch, so they go in a second post.
        var extras = measurements
            .Where(m => m.sensorCode is "meter_min" or "meter_c")
            .ToList();
        var main = measurements.Except(extras).ToList();

        using var response = await client.PostAsJsonAsync(
            $"{backend}/api/iot/sensor-data/batch",
            new { measurements = main },
            ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "BE telemetry forward failed for {DeviceCode}: {StatusCode}",
                snapshot.DeviceCode,
                response.StatusCode);
            return;
        }

        if (extras.Count > 0)
        {
            using var extraResponse = await client.PostAsJsonAsync(
                $"{backend}/api/iot/sensor-data/batch",
                new { measurements = extras },
                ct);
            if (!extraResponse.IsSuccessStatusCode)
                _logger.LogWarning(
                    "BE has no sensor for meter runtime or temperature yet ({StatusCode})",
                    extraResponse.StatusCode);
        }

        _lastForwarded[snapshot.DeviceCode] = DateTime.UtcNow;
    }

}
