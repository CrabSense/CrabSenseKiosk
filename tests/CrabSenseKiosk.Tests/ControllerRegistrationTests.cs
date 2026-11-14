using System.Net;
using System.Net.Http.Json;
using CrabSenseKiosk.Controllers;
using CrabSenseKiosk.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrabSenseKiosk.Tests;

public sealed class ControllerRegistrationTests
{
    [Theory]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(409, true)]
    [InlineData(503, false)]
    public async Task CloudFailure_DoesNotReuseCachedApproval(int status, bool blocked)
    {
        var registry = new ControllerRegistry();
        registry.Set("ESP-TEST", "Approved");
        using var transport = new ResponseHandler((HttpStatusCode)status);
        var api = Create(registry, transport);
        var result = Assert.IsType<ObjectResult>(await api.Register(new() { DeviceCode = "ESP-TEST" }, default));
        Assert.Equal(503, result.StatusCode);
        Assert.False(registry.IsAccepted("ESP-TEST"));
        Assert.Equal(blocked, registry.IsBlocked("ESP-TEST"));
    }

    [Fact]
    public async Task ConfirmedCloudApproval_CanRecoverAfterFailure()
    {
        var registry = new ControllerRegistry();
        registry.Set("ESP-TEST", "Pending");
        using var transport = new ResponseHandler(HttpStatusCode.OK);
        var api = Create(registry, transport);
        var result = Assert.IsType<ObjectResult>(await api.Register(new() { DeviceCode = "ESP-TEST" }, default));
        Assert.Equal(201, result.StatusCode);
        Assert.True(registry.IsAccepted("ESP-TEST"));
    }

    private static EdgeControllersController Create(ControllerRegistry registry, HttpMessageHandler handler)
        => new(registry, new Factory(handler),
            Options.Create(new EdgeGatewayOptions { BackendUrl = "https://cloud.example", KioskSecret = "test-kiosk-key" }),
            NullLogger<EdgeControllersController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class ResponseHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status)
            { Content = JsonContent.Create(new { success = true, data = new { edgeState = "Online" } }) });
    }
}
