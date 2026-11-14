using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CrabSenseKiosk.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CrabSenseKiosk.Controllers;

[ApiController]
public sealed class EdgeControllersController : ControllerBase
{
    private readonly IControllerRegistry _registry;
    private readonly IHttpClientFactory _http;
    private readonly EdgeGatewayOptions _options;
    private readonly ILogger<EdgeControllersController> _logger;
    private readonly ControllerEndpointRegistry? _endpoints;

    public EdgeControllersController(
        IControllerRegistry registry,
        IHttpClientFactory http,
        IOptions<EdgeGatewayOptions> options,
        ILogger<EdgeControllersController> logger,
        ControllerEndpointRegistry? endpoints = null)
    {
        _registry = registry;
        _http = http;
        _options = options.Value;
        _logger = logger;
        _endpoints = endpoints;
    }

    [HttpPost("/api/controllers/register")]
    public async Task<IActionResult> Register([FromBody] RegisterBody body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.ControllerId) && string.IsNullOrWhiteSpace(body.DeviceCode))
            return BadRequest(new { ok = false, error = "controller_id is required" });

        var code = (body.DeviceCode ?? body.ControllerId)!.Trim();
        var reported = await ReportAsync(code, body, ct);
        if (reported is null)
        {
            // Do not keep a cached approval after a failed cloud check.
            if (!_registry.IsBlocked(code)) _registry.Set(code, "Pending");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                ok = false, controller_id = code, status = "Pending",
                error = "Cloud registration unavailable; approval not confirmed."
            });
        }

        _registry.Set(code, reported.EdgeState);
        if (_registry.IsAccepted(code)) _endpoints?.Record(code, HttpContext.Connection.RemoteIpAddress);
        return StatusCode(StatusCodes.Status201Created, new
        {
            ok = true,
            controller_id = code,
            status = reported.EdgeState,
            secret = reported.Secret
        });
    }

    private async Task<BeRegister?> ReportAsync(string code, RegisterBody body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.KioskSecret) || string.IsNullOrWhiteSpace(_options.BackendUrl))
        {
            _logger.LogWarning("Controller {Code} stored locally. Kiosk has no credential yet.", code);
            return null;
        }

        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_options.BackendUrl.TrimEnd('/')}/api/controllers/register")
            {
                Content = JsonContent.Create(new
                {
                    deviceCode = code,
                    mac = body.Mac,
                    firmware = body.Firmware,
                    hardware = body.Hardware,
                    ipAddress = body.IpAddress
                })
            };
            request.Headers.Add("X-Kiosk-Key", _options.KioskSecret);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("BE register failed for {Code}: {Status}", code, response.StatusCode);
                if ((int)response.StatusCode is 401 or 403 or 409) _registry.Set(code, "Rejected");
                return null;
            }

            var envelope = await response.Content.ReadFromJsonAsync<BeEnvelope>(ct);
            return envelope?.Data;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "BE register unavailable for {Code}", code);
            return null;
        }
    }

    public sealed class RegisterBody
    {
        [JsonPropertyName("controller_id")]
        public string? ControllerId { get; set; }

        [JsonPropertyName("deviceCode")]
        public string? DeviceCode { get; set; }

        [JsonPropertyName("mac")]
        public string? Mac { get; set; }

        [JsonPropertyName("firmware")]
        public string? Firmware { get; set; }

        [JsonPropertyName("firmware_version")]
        public string? FirmwareVersion
        {
            set => Firmware ??= value;
        }

        [JsonPropertyName("hardware")]
        public string? Hardware { get; set; }

        [JsonPropertyName("hardware_version")]
        public string? HardwareVersion
        {
            set => Hardware ??= value;
        }

        [JsonPropertyName("ipAddress")]
        public string? IpAddress { get; set; }
    }

    private sealed class BeEnvelope
    {
        [JsonPropertyName("data")]
        public BeRegister? Data { get; set; }
    }

    private sealed class BeRegister
    {
        [JsonPropertyName("edgeState")]
        public string EdgeState { get; set; } = "Pending";

        [JsonPropertyName("secret")]
        public string? Secret { get; set; }
    }
}
