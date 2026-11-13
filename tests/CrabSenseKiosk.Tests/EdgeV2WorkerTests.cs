using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrabSenseKiosk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrabSenseKiosk.Tests;

public class EdgeV2WorkerTests
{
    private const string Key = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly EdgeV2Device Device = new()
    {
        DeviceCode = "ESP-TEST", LocalIp = "192.168.1.4", DeviceKey = "cloud-key", LocalKey = Key
    };

    [Theory]
    [InlineData(false, false, 1, true)]
    [InlineData(true, false, 0, false)]
    [InlineData(false, true, 1, false)]
    public async Task Cycle_UsesSignedManifest_AndAcknowledgesOnlyCurrentAttempt(
        bool staleRevision, bool badSignature, int expectedCommands, bool acknowledged)
    {
        var transport = new Fake(staleRevision, badSignature);
        var worker = new EdgeV2Worker(new Factory(transport),
            Options.Create(new EdgeGatewayOptions()), NullLogger<EdgeV2Worker>.Instance);
        var cycle = () => worker.CycleAsync(Device, new Uri("https://cloud.example/"), default);
        if (badSignature) await Assert.ThrowsAsync<InvalidOperationException>(cycle);
        else await cycle();
        Assert.Equal(expectedCommands, transport.Commands);
        Assert.Equal(acknowledged, transport.AckStatus == "acknowledged");
        if (staleRevision) Assert.Equal("failed", transport.AckStatus);
        if (badSignature) Assert.Null(transport.AckStatus);
        Assert.True(transport.ManifestUploaded);
        if (!badSignature) Assert.True(transport.ObservationsUploaded);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Fake(bool stale, bool badSignature) : HttpMessageHandler
    {
        public int Commands;
        public string? AckStatus;
        public bool ManifestUploaded;
        public bool ObservationsUploaded;
        private readonly Guid id = Guid.NewGuid();
        private readonly Guid attempt = Guid.NewGuid();
        private const long Seq = 9007199254741001L;
        private static string Sign(byte[] bytes) => Convert.ToHexString(
            HMACSHA256.HashData(Encoding.ASCII.GetBytes(Key), bytes)).ToLowerInvariant();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var body = req.Content is null ? null : await req.Content.ReadAsByteArrayAsync(ct);
            if (req.RequestUri.Host == Device.LocalIp)
            {
                Assert.Equal(Sign(body!), req.Headers.GetValues("X-Kiosk-Signature").Single());
                using var doc = JsonDocument.Parse(body!);
                var e = doc.RootElement;
                var kind = e.GetProperty("kind").GetString();
                var output = new Dictionary<string, object>
                {
                    ["success"] = true, ["protocol"] = 2, ["deviceCode"] = Device.DeviceCode,
                    ["nonce"] = e.GetProperty("nonce").GetString()!, ["message"] = "applied",
                    ["outputs"] = Enumerable.Range(1,5).Select(i => new { channel = i, on = false }).ToArray()
                };
                if (kind == "inventory")
                    output["manifest"] = new { schemaVersion = 1, revision = 2,
                        ports = Enumerable.Range(1,5).Select(i => new { portId = $"ssr-{i}", channel = i.ToString(),
                            gpio = i + 10, kind = "ssr", direction = "output", discovery = "declared",
                            present = true, remoteControl = false }).ToArray() };
                if (kind == "command")
                {
                    Commands++;
                    Assert.Equal(5, e.GetProperty("channel").GetInt32());
                    Assert.Equal(Seq, e.GetProperty("commandSequence").GetInt64());
                    Assert.Equal(2, e.GetProperty("inventoryRevision").GetInt64());
                    Assert.Equal(attempt, e.GetProperty("deliveryAttemptId").GetGuid());
                    output["id"] = id.ToString(); output["deliveryAttemptId"] = attempt.ToString();
                    output["commandSequence"] = Seq;
                }
                var raw = JsonSerializer.SerializeToUtf8Bytes(output);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(raw) };
                response.Headers.Add("X-Kiosk-Signature", badSignature && kind == "command" ? new string('0',64) : Sign(raw));
                return response;
            }
            Assert.Equal("cloud-key", req.Headers.GetValues("X-Device-Key").Single());
            Assert.Equal("2", req.Headers.GetValues("X-Edge-Protocol").Single());
            if (path.EndsWith("/ports/manifest"))
            {
                Assert.Equal(HttpMethod.Put, req.Method);
                using var doc = JsonDocument.Parse(body!);
                Assert.Equal(5, doc.RootElement.GetProperty("ports").GetArrayLength());
                ManifestUploaded = true;
            }
            if (path.EndsWith("/pending"))
            {
                Assert.True(ManifestUploaded);
                return Response(new { success = true, data = new[] { new {
                    id, deliveryAttemptId = attempt, commandSequence = Seq, deviceCode = Device.DeviceCode,
                    status = "delivered", command = "off", channel = "5", inventoryRevision = stale ? 1 : 2,
                    expiresAt = DateTimeOffset.UtcNow.AddSeconds(60), leaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30)
                } } });
            }
            if (path.EndsWith("/ack"))
            {
                using var doc = JsonDocument.Parse(body!);
                Assert.Equal(attempt, doc.RootElement.GetProperty("deliveryAttemptId").GetGuid());
                AckStatus = doc.RootElement.GetProperty("status").GetString();
            }
            if (path.EndsWith("/ports/observations"))
            {
                using var doc = JsonDocument.Parse(body!);
                Assert.Equal(5, doc.RootElement.GetProperty("outputs").GetArrayLength());
                ObservationsUploaded = true;
            }
            return Response(new { success = true });
        }
        private static HttpResponseMessage Response(object data) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
    }
}
