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

    public EdgeCommandWorker(
        ITelemetryService telemetry,
        IHttpClientFactory httpClients,
        IOptions<EdgeGatewayOptions> options,
        ILogger<EdgeCommandWorker> logger)
    {
        _telemetry = telemetry;
        _httpClients = httpClients;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollOnceAsync(stoppingToken);
            await Task.Delay(
                TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds)),
                stoppingToken);
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var backend = _options.BackendUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(backend))
            return;

        foreach (var snapshot in _telemetry.GetAll())
        {
            if (string.IsNullOrWhiteSpace(snapshot.IpAddress))
                continue;

            try
            {
                var client = _httpClients.CreateClient();
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{backend}/api/edge/commands/pending" +
                    $"?deviceCode={Uri.EscapeDataString(snapshot.DeviceCode)}");
                request.Headers.Add("X-Edge-Key", _options.ApiKey);
                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "BE command poll failed for {DeviceCode}: {StatusCode}",
                        snapshot.DeviceCode,
                        response.StatusCode);
                    continue;
                }

                var envelope = await response.Content
                    .ReadFromJsonAsync<ApiEnvelope<List<EdgeCommand>>>(cancellationToken: ct);
                foreach (var command in envelope?.Data ?? [])
                    await ForwardAsync(snapshot, command, backend, ct);
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

    private async Task ForwardAsync(
        DTOs.TelemetrySnapshot snapshot,
        EdgeCommand command,
        string backend,
        CancellationToken ct)
    {
        var client = _httpClients.CreateClient();
        var baseUrl = snapshot.IpAddress!.TrimEnd('/');
        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            baseUrl = $"http://{baseUrl}";

        var delivered = false;
        string? message = null;
        try
        {
            var response = await client.PostAsJsonAsync(
                $"{baseUrl}/api/command",
                new
                {
                    command = command.Command,
                    channel = command.Channel,
                    commandId = command.Id,
                    correlationId = command.CorrelationId
                },
                ct);
            delivered = response.IsSuccessStatusCode;
            message = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            message = ex.Message;
        }

        using var ack = new HttpRequestMessage(
            HttpMethod.Post,
            $"{backend}/api/edge/commands/{command.Id}/ack")
        {
            Content = JsonContent.Create(new
            {
                status = delivered ? "acknowledged" : "failed",
                message
            })
        };
        ack.Headers.Add("X-Edge-Key", _options.ApiKey);
        await client.SendAsync(ack, ct);
        _logger.LogInformation(
            "Command {CommandId} for {DeviceCode}: {Status}",
            command.Id,
            snapshot.DeviceCode,
            delivered ? "acknowledged" : "failed");
    }

    private sealed class ApiEnvelope<T>
    {
        public bool Success { get; set; }
        public T? Data { get; set; }
    }

    private sealed class EdgeCommand
    {
        public Guid Id { get; set; }
        public string Command { get; set; } = string.Empty;
        public string? Channel { get; set; }
        public string? CorrelationId { get; set; }
    }
}
