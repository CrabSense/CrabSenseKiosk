using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace CrabSenseKiosk.Services;

// A kiosk heartbeat proves gateway-to-cloud reachability, not camera/ESP32 health.
public sealed class KioskHeartbeatWorker(IHttpClientFactory clients,IOptions<EdgeGatewayOptions> options,ILogger<KioskHeartbeatWorker> logger,KioskMonitoringMetrics? metrics=null):BackgroundService
{
    private readonly EdgeGatewayOptions settings=options.Value;
    internal static Uri? Endpoint(string backend)
    {
        if(!Uri.TryCreate(backend.TrimEnd('/')+"/",UriKind.Absolute,out var uri)||
            (uri.Scheme!="https"&&!(uri.Scheme=="http"&&uri.IsLoopback))||
            !string.IsNullOrEmpty(uri.UserInfo)||!string.IsNullOrEmpty(uri.Query)||!string.IsNullOrEmpty(uri.Fragment))return null;
        return new Uri(uri,"api/kiosks/heartbeat");
    }
    internal async Task<HttpStatusCode?> SendOnceAsync(CancellationToken ct)
    {
        var endpoint=Endpoint(settings.BackendUrl);
        if(endpoint==null||string.IsNullOrWhiteSpace(settings.KioskSecret))return null;
        using var client=clients.CreateClient("kiosk-heartbeat");
        client.Timeout=TimeSpan.FromSeconds(8);
        using var req=new HttpRequestMessage(HttpMethod.Post,endpoint);
        req.Headers.Add("X-Kiosk-Key",settings.KioskSecret.Trim());
        req.Content=JsonContent.Create(new {lanIp=string.IsNullOrWhiteSpace(settings.KioskLanIp)?null:settings.KioskLanIp.Trim()});
        using var res=await client.SendAsync(req,ct);
        return res.StatusCode;
    }
    internal async Task<HttpStatusCode?> SendMetricsOnceAsync(CancellationToken ct)
    {
        var endpoint=Endpoint(settings.BackendUrl);
        if(endpoint==null||metrics==null||string.IsNullOrWhiteSpace(settings.KioskSecret))return null;
        using var client=clients.CreateClient("kiosk-heartbeat");
        client.Timeout=TimeSpan.FromSeconds(8);
        using var req=new HttpRequestMessage(HttpMethod.Post,new Uri(endpoint,"monitoring"));
        req.Headers.Add("X-Kiosk-Key",settings.KioskSecret.Trim());
        req.Content=JsonContent.Create(metrics.Snapshot());
        using var res=await client.SendAsync(req,ct);
        return res.StatusCode;
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!settings.EnableKioskHeartbeat||Endpoint(settings.BackendUrl)==null||string.IsNullOrWhiteSpace(settings.KioskSecret)){
            logger.LogInformation("Kiosk heartbeat disabled or not provisioned; no health report sent.");return;
        }
        while(!ct.IsCancellationRequested){
            try{
                var status=await SendOnceAsync(ct);
                if(status is not null && (int)status>=400)logger.LogWarning("Kiosk heartbeat rejected: {Status}",status);
                else if(status is not null && (int)status>=200&&(int)status<300&&metrics!=null){
                    var report=await SendMetricsOnceAsync(ct);if(report is not null&&(int)report>=400&&report!=HttpStatusCode.NotFound)logger.LogWarning("Kiosk monitoring rejected: {Status}",report);
                }
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogWarning("Kiosk heartbeat unavailable: {ErrorType}",ex.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(settings.KioskHeartbeatIntervalSeconds,15,60)),ct);
        }
    }
}
