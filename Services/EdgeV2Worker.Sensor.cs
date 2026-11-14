using System.Collections.Concurrent;
using System.Text.Json;

namespace CrabSenseKiosk.Services;

public sealed partial class EdgeV2Worker
{
    private sealed record SampleSchedule(int Version, DateTimeOffset Due);
    private sealed record SensorSample(bool Success, decimal? Value, string? Unit, DateTimeOffset? MeasuredAt, string Message);
    private readonly ConcurrentDictionary<(string Device, Guid Sensor), SampleSchedule> _sensorSchedule = new();

    private static string? SensorText(JsonElement json, string key) =>
        json.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<SensorSample> ReadSensorAsync(HttpClient client, EdgeV2Device device, int revision,
        JsonElement sensor, CancellationToken ct)
    {
        var code = SensorText(sensor, "sensorCode");
        var port = SensorText(sensor, "portCode");
        var unit = SensorText(sensor, "unit");
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(port) || string.IsNullOrWhiteSpace(unit))
            return new(false, null, null, null, "Missing registered port or unit");
        var request = Envelope(device, "sensor-read", 10);
        request["sensorCode"] = code;
        request["portCode"] = port;
        request["unit"] = unit;
        request["inventoryRevision"] = revision;
        request["busAddress"] = SensorText(sensor, "busAddress") ?? "";
        request["busRegister"] = SensorText(sensor, "busRegister") ?? "";
        var reply = await LocalAsync(client, device, request, ct);
        if (SensorText(reply, "sensorCode") != code || SensorText(reply, "portCode") != port)
            throw new InvalidOperationException("Sensor response identity mismatch.");
        if (!reply.GetProperty("success").GetBoolean())
            return new(false, null, null, null, "Controller could not read registered sensor");
        if (SensorText(reply, "unit") != unit || !reply.TryGetProperty("value", out var raw) || !raw.TryGetDecimal(out var value)
            || !reply.TryGetProperty("measuredEpoch", out var timestamp) || !timestamp.TryGetInt64(out var seconds))
            throw new InvalidOperationException("Invalid signed sensor measurement.");
        var measured = DateTimeOffset.FromUnixTimeSeconds(seconds);
        var now = DateTimeOffset.UtcNow;
        if (measured < now.AddSeconds(-5) || measured > now.AddSeconds(1))
            throw new InvalidOperationException("Stale signed sensor measurement.");
        return new(true, value, unit, measured, "Controller read");
    }

    internal async Task SensorCycleAsync(HttpClient client, Uri cloud, EdgeV2Device device, int revision, CancellationToken ct)
    {
        var query = Uri.EscapeDataString(device.DeviceCode);
        var configs = (await CloudAsync(client, cloud, device, HttpMethod.Get,
            "api/iot/sensors/configuration?deviceCode=" + query, null, ct)).GetProperty("data").EnumerateArray().ToArray();
        var ids = configs.Select(s => s.GetProperty("id").GetGuid()).ToHashSet();
        foreach (var key in _sensorSchedule.Keys.Where(k => k.Device == device.DeviceCode && !ids.Contains(k.Sensor)))
            _sensorSchedule.TryRemove(key, out _);
        var now = DateTimeOffset.UtcNow;
        // Process oldest due samples first, bounded to avoid starving the heartbeat.
        foreach (var sensor in configs.OrderBy(s => _sensorSchedule.TryGetValue((device.DeviceCode, s.GetProperty("id").GetGuid()), out var q)
            ? q.Due : DateTimeOffset.MinValue).Take(64))
        {
            ct.ThrowIfCancellationRequested();
            var id = sensor.GetProperty("id").GetGuid();
            var key = (device.DeviceCode, id);
            var version = sensor.GetProperty("configurationVersion").GetInt32();
            if (!sensor.GetProperty("isActive").GetBoolean()) { _sensorSchedule.TryRemove(key, out _); continue; }
            var interval = sensor.TryGetProperty("samplingIntervalSeconds", out var configured) && configured.ValueKind == JsonValueKind.Number
                ? configured.GetInt32() : 10;
            // The Kiosk loop cannot guarantee a 1s cadence. Never acknowledge it.
            if (interval < 2 || interval > 86400) continue;
            if (_sensorSchedule.TryGetValue(key, out var scheduled) && scheduled.Version == version && scheduled.Due > now) continue;
            try
            {
                var sample = await ReadSensorAsync(client, device, revision, sensor, ct);
                if (sample.Success)
                {
                    await CloudAsync(client, cloud, device, HttpMethod.Post, "api/iot/sensor-data", new {
                        deviceCode = device.DeviceCode, sensorCode = SensorText(sensor, "sensorCode"),
                        value = sample.Value, unit = sample.Unit, measuredAt = sample.MeasuredAt,
                        eventId = Guid.NewGuid().ToString("N")
                    }, ct);
                    _sensorSchedule[key] = new(version, DateTimeOffset.UtcNow.AddSeconds(interval));
                    var applied = sensor.TryGetProperty("appliedConfigurationVersion", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0;
                    if (version > 0 && applied != version)
                        await CloudAsync(client, cloud, device, HttpMethod.Put, $"api/iot/sensors/{id}/configuration/ack",
                            new { deviceCode = device.DeviceCode, version }, ct);
                }
                else
                {
                    await CloudAsync(client, cloud, device, HttpMethod.Post, $"api/iot/sensors/{id}/errors",
                        new { deviceCode = device.DeviceCode, errorCode = "ReadFailed" }, ct);
                    _sensorSchedule[key] = new(version, DateTimeOffset.UtcNow.AddSeconds(interval));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning("Sensor sample failed for {Code}: {Type}", device.DeviceCode, ex.GetType().Name);
                _sensorSchedule[key] = new(version, DateTimeOffset.UtcNow.AddSeconds(interval));
            }
        }
        var pending = (await CloudAsync(client, cloud, device, HttpMethod.Get,
            "api/iot/sensor-tests/pending?deviceCode=" + query, null, ct)).GetProperty("data").EnumerateArray();
        foreach (var test in pending)
        {
            if (test.GetProperty("expiresAt").GetDateTimeOffset() <= DateTimeOffset.UtcNow) continue;
            try
            {
                var sample = await ReadSensorAsync(client, device, revision, test, ct);
                var id = test.GetProperty("id").GetGuid();
                await CloudAsync(client, cloud, device, HttpMethod.Put, $"api/iot/sensor-tests/{id}/result",
                    new { deviceCode = device.DeviceCode, success = sample.Success, value = sample.Value,
                        unit = sample.Unit, measuredAt = sample.MeasuredAt, message = sample.Message }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log.LogWarning("Sensor test failed to report for {Code}: {Type}", device.DeviceCode, ex.GetType().Name); }
        }
    }
}
