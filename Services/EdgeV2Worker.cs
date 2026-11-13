using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CrabSenseKiosk.Services;

// Trusted provisioning settings, NOT addresses supplied by unauthenticated telemetry.
public sealed class EdgeV2Device
{
    public string DeviceCode { get; set; } = "";
    public string LocalIp { get; set; } = "";
    public string DeviceKey { get; set; } = "";
    public string LocalKey { get; set; } = "";
}

public sealed class EdgeV2Worker : BackgroundService
{
    private readonly IHttpClientFactory _clients;
    private readonly EdgeGatewayOptions _options;
    private readonly ILogger<EdgeV2Worker> _log;
    private readonly ControllerEndpointRegistry? _endpoints;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public EdgeV2Worker(IHttpClientFactory clients, IOptions<EdgeGatewayOptions> options, ILogger<EdgeV2Worker> log,
        ControllerEndpointRegistry? endpoints = null)
        => (_clients, _options, _log, _endpoints) = (clients, options.Value, log, endpoints);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Independent loops ensure one offline board cannot starve another board's heartbeat.
        var devices = _options.EdgeV2Devices
            .Where(d => !string.IsNullOrWhiteSpace(d.DeviceCode))
            .GroupBy(d => d.DeviceCode, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1).Select(g => g.Single()).ToArray();
        await Task.WhenAll(devices.Select(d => RunDeviceAsync(d, stoppingToken)));
    }

    private async Task RunDeviceAsync(EdgeV2Device d, CancellationToken ct)
    {
        if (!Valid(d) || !Uri.TryCreate(_options.BackendUrl.TrimEnd('/') + "/", UriKind.Absolute, out var cloud)
            || cloud.Scheme != "https" || cloud.UserInfo.Length != 0 || cloud.Query.Length != 0 || cloud.Fragment.Length != 0)
        {
            _log.LogWarning("Edge V2 device {Code} not configured securely; disabled.", d.DeviceCode);
            return;
        }
        string? lastVerified = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var address = !string.IsNullOrWhiteSpace(d.LocalIp) ? d.LocalIp
                    : lastVerified ?? _endpoints?.Candidate(d.DeviceCode);
                if (address is not null && ControllerEndpointRegistry.IsPrivate(address))
                {
                    var target = new EdgeV2Device { DeviceCode = d.DeviceCode, DeviceKey = d.DeviceKey, LocalKey = d.LocalKey, LocalIp = address };
                    await CycleAsync(target, cloud, ct);
                    lastVerified = address;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { lastVerified = null; _log.LogWarning("Edge V2 cycle failed for {Code}: {Type}", d.DeviceCode, ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    internal async Task CycleAsync(EdgeV2Device d, Uri cloud, CancellationToken ct)
    {
        using var client = _clients.CreateClient("controller-inventory");
        client.Timeout = TimeSpan.FromSeconds(3);
        var inventory = await LocalAsync(client, d, Envelope(d, "inventory", 10), ct);
        if (!inventory.GetProperty("success").GetBoolean()) throw new InvalidOperationException("Inventory rejected.");
        var manifest = inventory.GetProperty("manifest");
        var revision = manifest.GetProperty("revision").GetInt64();
        if (revision < 1 || manifest.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidOperationException("Invalid manifest.");
        var channels = manifest.GetProperty("ports").EnumerateArray()
            .Where(p => p.GetProperty("present").GetBoolean() && p.GetProperty("direction").GetString() == "output"
                && p.GetProperty("kind").GetString() is "ssr" or "relay")
            .Select(p => int.Parse(p.GetProperty("channel").GetString()!, System.Globalization.CultureInfo.InvariantCulture))
            .ToHashSet();
        if (channels.Count == 0 || channels.Any(c => c < 1 || c > 16))
            throw new InvalidOperationException("Invalid channel inventory.");

        await CloudAsync(client, cloud, d, HttpMethod.Put,
            $"api/controllers/{Uri.EscapeDataString(d.DeviceCode)}/ports/manifest", manifest, ct);
        var heartbeat = await LocalAsync(client, d, Envelope(d, "heartbeat", 5), ct);
        if (!heartbeat.GetProperty("success").GetBoolean()) throw new InvalidOperationException("Heartbeat rejected.");

        var latest = heartbeat;
        var pending = await CloudAsync(client, cloud, d, HttpMethod.Get,
            $"api/edge/commands/pending?deviceCode={Uri.EscapeDataString(d.DeviceCode)}", null, ct);
        foreach (var cmd in pending.GetProperty("data").EnumerateArray().OrderBy(x => x.GetProperty("commandSequence").GetInt64()))
        {
            var id = cmd.GetProperty("id").GetGuid();
            var attempt = cmd.GetProperty("deliveryAttemptId").GetGuid();
            var seq = cmd.GetProperty("commandSequence").GetInt64();
            var action = cmd.GetProperty("command").GetString();
            var now = DateTimeOffset.UtcNow;
            var expires = cmd.GetProperty("expiresAt").GetDateTimeOffset();
            var lease = cmd.GetProperty("leaseExpiresAt").GetDateTimeOffset();
            var deadline = new[] { expires, lease, now.AddSeconds(10) }.Min();
            if (cmd.GetProperty("deviceCode").GetString() != d.DeviceCode
                || cmd.GetProperty("status").GetString() != "delivered"
                || seq <= 0 || id == Guid.Empty || attempt == Guid.Empty || deadline <= now
                || action is not ("on" or "off" or "alloff"))
                throw new InvalidOperationException("Invalid leased command.");
            var channel = 0;
            var validChannel = action == "alloff"
                ? cmd.GetProperty("channel").ValueKind == JsonValueKind.Null
                : int.TryParse(cmd.GetProperty("channel").GetString(), out channel) && channels.Contains(channel);
            if (!validChannel || !cmd.TryGetProperty("inventoryRevision", out var rev) ||
                rev.ValueKind != JsonValueKind.Number || rev.GetInt64() != revision)
            {
                await AckAsync(client, cloud, d, id, attempt, false, "Inventory revision/channel mismatch", ct);
                continue;
            }
            var env = Envelope(d, "command", 10);
            env["id"] = id.ToString();
            env["deliveryAttemptId"] = attempt.ToString();
            env["commandSequence"] = seq;
            env["command"] = action!;
            env["channel"] = channel;
            env["inventoryRevision"] = revision;
            env["expiresEpoch"] = deadline.ToUnixTimeSeconds();
            // A transport failure or invalid signature never acknowledges a command.
            var result = await LocalAsync(client, d, env, ct);
            if (result.GetProperty("id").GetGuid() != id || result.GetProperty("deliveryAttemptId").GetGuid() != attempt
                || result.GetProperty("commandSequence").GetInt64() != seq)
                throw new InvalidOperationException("Command response mismatch.");
            await AckAsync(client, cloud, d, id, attempt, result.GetProperty("success").GetBoolean(),
                result.GetProperty("message").GetString() ?? "", ct);
            if (result.TryGetProperty("outputs", out _)) latest = result;
        }
        await ReportStatesAsync(client, cloud, d, latest, channels, revision, ct);
    }

    private static Dictionary<string, object> Envelope(EdgeV2Device d, string kind, int seconds) => new()
    {
        ["protocol"] = 2, ["kind"] = kind, ["deviceCode"] = d.DeviceCode,
        ["nonce"] = Guid.NewGuid().ToString(), ["expiresEpoch"] = DateTimeOffset.UtcNow.AddSeconds(seconds).ToUnixTimeSeconds()
    };

    private static async Task<JsonElement> LocalAsync(HttpClient client, EdgeV2Device d, Dictionary<string, object> env, CancellationToken ct)
    {
        var raw = JsonSerializer.SerializeToUtf8Bytes(env, Json);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{d.LocalIp}/api/command");
        request.Content = new ByteArrayContent(raw);
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Kiosk-Signature", Sign(d.LocalKey, raw));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBoundedAsync(response, ct);
        if (!response.Headers.TryGetValues("X-Kiosk-Signature", out var values) || values.SingleOrDefault() is not string signature
            || signature.Length != 64 || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(Sign(d.LocalKey, bytes))))
            throw new InvalidOperationException("Invalid ESP signature.");
        using var doc = JsonDocument.Parse(bytes);
        var data = doc.RootElement;
        if (data.GetProperty("protocol").GetInt32() != 2
            || data.GetProperty("deviceCode").GetString() != d.DeviceCode
            || data.GetProperty("nonce").GetString() != (string)env["nonce"])
            throw new InvalidOperationException("ESP response identity mismatch.");
        return data.Clone();
    }

    private static async Task<JsonElement> CloudAsync(HttpClient client, Uri cloud, EdgeV2Device d, HttpMethod method,
        string path, object? data, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(cloud, path));
        request.Headers.Add("X-Device-Key", d.DeviceKey);
        request.Headers.Add("X-Edge-Protocol", "2");
        if (data is not null) request.Content = JsonContent.Create(data, options: Json);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBoundedAsync(response, ct);
        using var doc = JsonDocument.Parse(bytes);
        if (!doc.RootElement.GetProperty("success").GetBoolean())
            throw new InvalidOperationException("Cloud rejected request.");
        return doc.RootElement.Clone();
    }

    private static Task<JsonElement> AckAsync(HttpClient client, Uri cloud, EdgeV2Device d, Guid id, Guid attempt,
        bool success, string message, CancellationToken ct) =>
        CloudAsync(client, cloud, d, HttpMethod.Post, $"api/edge/commands/{id}/ack",
            new { status = success ? "acknowledged" : "failed", deliveryAttemptId = attempt, message }, ct);

    private async Task ReportStatesAsync(HttpClient client, Uri cloud, EdgeV2Device d, JsonElement result,
        HashSet<int> channels, long revision, CancellationToken ct)
    {
        var states = result.GetProperty("outputs").EnumerateArray().ToArray();
        if (states.Length != channels.Count || !states.Select(s => s.GetProperty("channel").GetInt32()).ToHashSet().SetEquals(channels))
            throw new InvalidOperationException("Invalid observed channels.");
        await CloudAsync(client, cloud, d, HttpMethod.Put,
            $"api/controllers/{Uri.EscapeDataString(d.DeviceCode)}/ports/observations",
            new { revision, outputs = states.Select(s => new {
                channel = s.GetProperty("channel").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
                on = s.GetProperty("on").GetBoolean()
            }).ToArray() }, ct);
        // Preserve legacy RAS status for already mapped equipment. The inventory
        // itself stores every output, including unassigned ports.
        foreach (var state in states)
        {
            try
            {
                await CloudAsync(client, cloud, d, HttpMethod.Post, "api/iot/relay-state",
                    new { deviceCode = d.DeviceCode, channel = state.GetProperty("channel").GetInt32(), on = state.GetProperty("on").GetBoolean() }, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
            {
                // Older BE accepts only existing RAS bindings. An unbound port is not a failed command.
                _log.LogDebug("Relay observation is not mapped for {Code}.", d.DeviceCode);
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int n;
        while ((n = await input.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
        {
            if (output.Length + n > 262144) throw new InvalidOperationException("Response exceeds limit.");
            output.Write(buffer, 0, n);
        }
        return output.ToArray();
    }

    private static string Sign(string key, byte[] raw) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.ASCII.GetBytes(key), raw)).ToLowerInvariant();

    private static bool Valid(EdgeV2Device d)
    {
        if (d.DeviceCode.Length > 100 || d.DeviceCode is "." or ".." || string.IsNullOrWhiteSpace(d.DeviceKey)
            || !System.Text.RegularExpressions.Regex.IsMatch(d.DeviceCode, @"\A[A-Za-z0-9_.:-]+\z")
            || d.LocalKey.Length != 64 || d.LocalKey.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            || (!string.IsNullOrWhiteSpace(d.LocalIp) && !ControllerEndpointRegistry.IsPrivate(d.LocalIp))) return false;
        return true;
    }
}
