using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CrabSenseKiosk.Services.WaterBench;

// One serially awaited POST per poll for typed observations or scoped JPEGs, never commands.
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
        BenchSyncQueue queue,CancellationToken ct,bool enableMedia=false)
    {
        if(endpoint!=Endpoint(endpoint.GetLeftPart(UriPartial.Authority))||
            string.IsNullOrWhiteSpace(secret)||secret.Length>4096||secret.Any(char.IsControl))
            return new("not_provisioned");
        var plan=queue.Scan();
        // Use the same worker for both routes; timeline events always take precedence.
        var next=plan.Ready.FirstOrDefault(x=>x.Kind=="batch")??
            (enableMedia?plan.Ready.FirstOrDefault(x=>x.Kind=="media"):null);
        if(next is null)return new(plan.InvalidFiles>0||plan.BlockedBatches>0?"queue_blocked":
            plan.Pending>0?"waiting_for_media":"idle");
        var now=DateTimeOffset.UtcNow;
        bool media=next.Kind=="media";
        queue.TransportStatus(media?"sending_media":"sending",attempt:now);
        try
        {
            var body=queue.Prepare(next);
            if(!media&&(body is not BenchBatchEvent batch||batch.EventId!=next.EventId||batch.StationId!=next.StationId))
                return new("local_evidence_invalid");
            var bytesToSend=JsonSerializer.SerializeToUtf8Bytes(body,body.GetType(),BenchMediaManifest.Json);
            if(bytesToSend.Length>(media?96*1024:8192))return new("local_evidence_invalid");
            // Fixed same-origin paths; never follow a response-supplied URL.
            var target=media?new Uri(endpoint,"/api/edge/water-bench/media"):endpoint;
            using var request=new HttpRequestMessage(HttpMethod.Post,target);
            request.Headers.Add("X-Kiosk-Key",secret.Trim());
            request.Content=new ByteArrayContent(bytesToSend);
            request.Content.Headers.ContentType=new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            // Drive upload/readback needs a longer deadline than a small DB event.
            // The named HttpClient also bounds response buffering and disables redirects.
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(media?60:8));
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseContentRead,deadline.Token);
            int status=(int)response.StatusCode;
            if(response.StatusCode!=HttpStatusCode.OK)
                return new(status is 401 or 403?"credential_rejected":status==409?"backend_conflict":
                    status==429?"backend_rate_limited":status>=500?"backend_unavailable":"backend_rejected",status);
            if(response.Content.Headers.ContentType?.MediaType!="application/json")
                return new("invalid_receipt",status);
            var bytes=await response.Content.ReadAsByteArrayAsync(deadline.Token);
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
                if(eventId!=next.EventId||receiptId==Guid.Empty)return new("invalid_receipt",status);
                if(media)
                {
                    var mediaId=data.GetProperty("mediaId").GetGuid();
                    var sha256=data.GetProperty("sha256").GetString();
                    if(mediaId==Guid.Empty||sha256 is null||sha256!=next.ImageSha256)
                        return new("invalid_receipt",status);
                    receipt=new(eventId,receiptId,mediaId,sha256);
                }
                else
                {
                    if(data.TryGetProperty("mediaId",out _)||data.TryGetProperty("sha256",out _))
                        return new("invalid_receipt",status);
                    receipt=new(eventId,receiptId);
                }
            }
            catch(Exception ex) when(ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
            {return new("invalid_receipt",status);}
            // Atomic create-only receipt. If the process dies before this write, resend same event ID.
            queue.Accept(next,receipt,DateTimeOffset.UtcNow);
            queue.TransportStatus(media?"media_acknowledged":"acknowledged",receipt:DateTimeOffset.UtcNow,httpStatus:status);
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
                    var result=await BackendBenchBatchSync.SendNext(client,endpoint,secret,queue,ct,settings.EnableMediaSync);
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
