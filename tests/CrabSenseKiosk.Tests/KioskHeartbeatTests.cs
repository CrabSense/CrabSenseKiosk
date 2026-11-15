using System.Net;
using System.Text.Json;
using CrabSenseKiosk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
namespace CrabSenseKiosk.Tests;
public sealed class KioskHeartbeatTests
{
    [Theory]
    [InlineData("https://cloud.example",true)]
    [InlineData("http://127.0.0.1:5080",true)]
    [InlineData("http://192.168.1.10:5080",false)]
    [InlineData("https://user:pass@cloud.example",false)]
    [InlineData("https://cloud.example/?token=secret",false)]
    public void Endpoint_ProtectsCredential(string url,bool allowed)=>Assert.Equal(allowed,KioskHeartbeatWorker.Endpoint(url)!=null);
    [Fact]
    public async Task ProvisionedKiosk_SendsOnlyKioskCredentialAndLanIp(){
        using var handler=new Fake();
        var worker=new KioskHeartbeatWorker(new Factory(handler),Options.Create(new EdgeGatewayOptions{BackendUrl="https://cloud.example",KioskSecret="kiosk-secret",KioskLanIp="192.168.1.12"}),NullLogger<KioskHeartbeatWorker>.Instance);
        Assert.Equal(HttpStatusCode.OK,await worker.SendOnceAsync(default));
        Assert.Equal(1,handler.Calls);
    }
    [Fact]
    public async Task UnprovisionedKiosk_DoesNotSendHeartbeat(){
        using var handler=new Fake();var worker=new KioskHeartbeatWorker(new Factory(handler),Options.Create(new EdgeGatewayOptions{BackendUrl="https://cloud.example"}),NullLogger<KioskHeartbeatWorker>.Instance);
        Assert.Null(await worker.SendOnceAsync(default));Assert.Equal(0,handler.Calls);
    }
    private sealed class Factory(HttpMessageHandler handler):IHttpClientFactory{public HttpClient CreateClient(string name)=>new(handler,disposeHandler:false);}
    private sealed class Fake:HttpMessageHandler{
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct){
            Calls++;Assert.Equal("/api/kiosks/heartbeat",req.RequestUri!.AbsolutePath);Assert.Equal(HttpMethod.Post,req.Method);
            Assert.Equal("kiosk-secret",req.Headers.GetValues("X-Kiosk-Key").Single());Assert.False(req.Headers.Contains("X-Device-Key"));
            using var json=JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));Assert.Equal("192.168.1.12",json.RootElement.GetProperty("lanIp").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
