using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrabSenseKiosk.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CrabSenseKiosk.Controllers;

[ApiController]
public sealed class ControllerPortsController : ControllerBase
{
    private readonly IHttpClientFactory _clients;
    private readonly EdgeGatewayOptions _options;

    public ControllerPortsController(IHttpClientFactory clients, IOptions<EdgeGatewayOptions> options)
        => (_clients, _options) = (clients, options.Value);

    // Cloud is authoritative: no local approval or shared-key fallback for inventory.
    [HttpPut("/api/controllers/{deviceCode}/ports/manifest")]
    [RequestSizeLimit(65536)]
    public async Task<IActionResult> Report(string deviceCode, [FromBody] JsonElement manifest, CancellationToken ct)
    {
        if (deviceCode.Length > 100 || !Regex.IsMatch(deviceCode, @"\A[A-Za-z0-9_.:-]+\z"))
            return BadRequest(new { message = "Invalid controller code." });
        var key = Request.Headers["X-Device-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256)
            return Unauthorized(new { message = "X-Device-Key required." });
        if (manifest.ValueKind != JsonValueKind.Object)
            return BadRequest(new { message = "Manifest must be an object." });
        if (!Uri.TryCreate(_options.BackendUrl.TrimEnd('/') + "/", UriKind.Absolute, out var backend)
            || backend.Scheme is not ("http" or "https"))
            return StatusCode(503, new { message = "Backend not configured." });

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put,
                new Uri(backend, $"api/controllers/{Uri.EscapeDataString(deviceCode)}/ports/manifest"))
            {
                Content = JsonContent.Create(manifest)
            };
            request.Headers.Add("X-Device-Key", key);
            using var client = _clients.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return new ContentResult
            {
                StatusCode = (int)response.StatusCode,
                ContentType = "application/json",
                Content = body
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(503, new { message = "Backend unavailable; controller must retry." });
        }
    }
}
