using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CrabSenseKiosk.Services.WaterBench;

// One serially awaited POST per poll. Only observation events, never commands or images.
internal sealed record BenchBatchSyncOutcome(string State,int? HttpStatus=null,bool Acknowledged=false);
internal static class BackendBenchBatchSync
{
    internal static Uri? Endpoint(string? root)
    {
        if(!Uri.TryCreate(root,UriKind.Absolute,out var uri)||uri.Scheme!="https"||
            string.IsNullOrEmpty(uri.Host)||!string.IsNullOrEmpty(uri.UserInfo)||!string.IsNullOrEmpty(uri.Query)||
            !string.IsNullOrEmpty(uri.Fragment)||uri.AbsolutePath is not ("/" or "/api" or "/api/"))return null;
        return new Uri(uri,"/api/edge/water-bench/batch-events");
    }
    internal static async Task<BenchBatchSyncOutcome> SendNext(HttpClient client,Uri endpoint,string secret,
        BenchSyncQueue queue,CancellationToken ct)
    {
        if(endpoint!=Endpoint(endpoint.GetLeftPart(UriPartial.Authority))||
            string.IsNullOrWhiteSpace(secret)||secret.Length>4096||secret.Any(char.IsControl))
            return new("not_provisioned");
        var plan=queue.Scan();
        var next=plan.Ready.FirstOrDefault(x=>x.Kind=="batch");
        if(next is null)return new(plan.InvalidFiles>0||plan.BlockedBatches>0?"queue_blocked":
            plan.Pending>0?"waiting_for_media":"idle");
        var now=DateTimeOffset.UtcNow;
        queue.TransportStatus("sending",attempt:now);
        try
        {
            if(queue.Prepare(next) is not BenchBatchEvent body||body.EventId!=next.EventId||body.StationId!=next.StationId)
                return new("local_evidence_invalid");
            using var request=new HttpRequestMessage(HttpMethod.Post,endpoint);
            request.Headers.Add("X-Kiosk-Key",secret.Trim());
            request.Content=JsonContent.Create(body,options:BenchMediaManifest.Json);
            // The configured client disables redirects and bounds buffered response size.
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseContentRead,ct);
            int status=(int)response.StatusCode;
            if(response.StatusCode!=HttpStatusCode.OK)
                return new(status is 401 or 403?"credential_rejected":status==409?"backend_conflict":
                    status==429?"backend_rate_limited":status>=500?"backend_unavailable":"backend_rejected",status);
            if(response.Content.Headers.ContentType?.MediaType!="application/json")
                return new("invalid_receipt",status);
            var bytes=await response.Content.ReadAsByteArrayAsync(ct);
            if(bytes.Length is <2 or >16384)return new("invalid_receipt",status);
            BenchSyncServerReceipt receipt;
            try
            {
                using var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=16});
                var root=doc.RootElement;
                if(root.ValueKind!=JsonValueKind.Object||!root.GetProperty("success").GetBoolean())return new("invalid_receipt",status);
                var data=root.GetProperty("data");
                var eventId=data.GetProperty("eventId").GetGuid();
                var receiptId=data.GetProperty("receiptId").GetGuid();
                // The backend returns duplicate=true on a retry of an already committed event.
                _=data.GetProperty("duplicate").GetBoolean();
                if(eventId!=next.EventId||receiptId==Guid.Empty||data.TryGetProperty("mediaId",out _)||data.TryGetProperty("sha256",out _))
                    return new("invalid_receipt",status);
                receipt=new(eventId,receiptId);
            }
            catch(Exception ex) when(ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
            {return new("invalid_receipt",status);}
            // Atomic create-only receipt. If the process dies before this write, resend same event ID.
            queue.Accept(next,receipt,DateTimeOffset.UtcNow);
            queue.TransportStatus("acknowledged",receipt:DateTimeOffset.UtcNow,httpStatus:status);
            return new("acknowledged",status,true);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(OperationCanceledException){return new("transport_timeout");}
        catch(HttpRequestException){return new("transport_unavailable");}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException or InvalidOperationException)
        {return new("local_evidence_or_receipt_error");}
    }
}

public sealed class BackendBenchBatchSyncWorker(IHttpClientFactory clients,IOptions<EdgeGatewayOptions> gateway,
    IOptions<WaterBenchOptions> options,WaterBenchSession session,ILogger<BackendBenchBatchSyncWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var settings=options.Value;
        if(!settings.Enabled||!settings.EnableBatchSync)return;
        var queue=session.SyncQueue;
        while(!ct.IsCancellationRequested)
        {
            var delay=TimeSpan.FromSeconds(30);
            try
            {
                var endpoint=BackendBenchBatchSync.Endpoint(gateway.Value.BackendUrl);
                var secret=gateway.Value.KioskSecret;
                if(endpoint is null||string.IsNullOrWhiteSpace(secret)||settings.BackendStationId==Guid.Empty)
                    queue.TransportStatus("not_provisioned");
                else
                {
                    using var client=clients.CreateClient("water-bench-batch-sync");
                    var result=await BackendBenchBatchSync.SendNext(client,endpoint,secret,queue,ct);
                    if(!result.Acknowledged)queue.TransportStatus(result.State,httpStatus:result.HttpStatus);
                    delay=result.Acknowledged?TimeSpan.FromSeconds(1):
                        result.State is "idle" or "waiting_for_media"?TimeSpan.FromSeconds(10):TimeSpan.FromSeconds(30);
                }
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex)
            {
                // Never log request body, server response, URL or credential.
                queue.TransportStatus("worker_error");
                log.LogWarning("WaterBench batch sync failed: {Type}",ex.GetType().Name);
            }
            try{await Task.Delay(delay,ct);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
        }
    }
}
