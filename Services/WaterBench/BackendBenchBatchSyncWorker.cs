using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CrabSenseKiosk.Services.WaterBench;

// One serially awaited POST per poll for typed observations or scoped JPEGs, never commands.
internal sealed record BenchBatchSyncOutcome(string State,int? HttpStatus=null,bool Acknowledged=false,int? RetryAfterSeconds=null);
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
        BenchSyncQueue queue,CancellationToken ct,bool enableMedia=false,bool enableNh3=false,BenchSyncRetryJournal? retry=null)
    {
        if(endpoint!=Endpoint(endpoint.GetLeftPart(UriPartial.Authority))||
            string.IsNullOrWhiteSpace(secret)||secret.Length>4096||secret.Any(char.IsControl))
            return new("not_provisioned");
        var plan=queue.Scan();
        var now=DateTimeOffset.UtcNow;
        // Preserve per-batch prerequisites from the queue; choose oldest eligible work
        // across batches, rather than starving JPEGs behind newer timeline events.
        var enabled=plan.Ready.Where(x=>x.Kind=="batch"||x.Kind=="nh3"&&enableNh3||x.Kind=="media"&&enableMedia).ToArray();
        if(retry!=null)
        {
            retry.Reconcile(queue);
            var status=retry.Status(now);queue.RetryStatus(status.Deferred,status.NextRetryAt,status.GlobalUntil);
            if(retry.GlobalPaused(now))return new("global_retry_wait");
        }
        var next=retry==null?(enabled.FirstOrDefault(x=>x.Kind=="batch")??enabled.FirstOrDefault(x=>x.Kind=="nh3")??enabled.FirstOrDefault(x=>x.Kind=="media")):
            enabled.FirstOrDefault(x=>retry.Eligible(x,now));
        if(next is null&&enabled.Length>0)return new("batch_retry_wait");
        if(next is null)return new(plan.InvalidFiles>0||plan.BlockedBatches>0?"queue_blocked":
            plan.Pending>0?(plan.Ready.Any(x=>x.Kind=="media")?"waiting_for_media":"waiting_for_nh3"):"idle");
        retry?.Begin(next,now);
        var outcome=await SendCandidate(client,endpoint,secret,queue,next,ct);
        if(retry!=null)
        {
            retry.Finish(next,outcome,DateTimeOffset.UtcNow);
            var status=retry.Status(DateTimeOffset.UtcNow);
            queue.RetryStatus(status.Deferred,status.NextRetryAt,status.GlobalUntil);
        }
        return outcome;
    }
    internal static int? RetryAfter(HttpResponseMessage response,DateTimeOffset now)
    {
        var header=response.Headers.RetryAfter;
        var delay=header?.Delta??(header?.Date is {} date?date-now:(TimeSpan?)null);
        return delay.HasValue?(int)Math.Clamp(Math.Ceiling(delay.Value.TotalSeconds),0,86400):null;
    }
    private static async Task<BenchBatchSyncOutcome> SendCandidate(HttpClient client,Uri endpoint,string secret,
        BenchSyncQueue queue,BenchSyncCandidate next,CancellationToken ct)
    {
        var now=DateTimeOffset.UtcNow;
        bool media=next.Kind=="media",nh3=next.Kind=="nh3";
        queue.TransportStatus(media?"sending_media":nh3?"sending_nh3":"sending",attempt:now);
        try
        {
            var body=queue.Prepare(next);
            if(!media&&!nh3&&(body is not BenchBatchEvent batch||batch.EventId!=next.EventId||batch.StationId!=next.StationId)||
                nh3&&(body is not Nh3EvidenceEvent n||n.EventId!=next.EventId||n.StationId!=next.StationId))
                return new("local_evidence_invalid");
            var bytesToSend=JsonSerializer.SerializeToUtf8Bytes(body,body.GetType(),BenchMediaManifest.Json);
            if(bytesToSend.Length>(media?96*1024:8192))return new("local_evidence_invalid");
            // Fixed same-origin paths; never follow a response-supplied URL.
            var target=media?new Uri(endpoint,"/api/edge/water-bench/media"):
                nh3?new Uri(endpoint,"/api/edge/water-bench/nh3-evidence"):endpoint;
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
                    status==429?"backend_rate_limited":status>=500?"backend_unavailable":"backend_rejected",status,
                    RetryAfterSeconds:RetryAfter(response,DateTimeOffset.UtcNow));
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
            queue.TransportStatus(media?"media_acknowledged":nh3?"nh3_acknowledged":"acknowledged",receipt:DateTimeOffset.UtcNow,httpStatus:status);
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
        var rootEndpoint=BackendBenchBatchSync.Endpoint(gateway.Value.BackendUrl);
        if(rootEndpoint is null||settings.BackendStationId==Guid.Empty){queue.TransportStatus("not_provisioned");return;}
        BenchSyncRetryJournal journal;
        try{journal=new(queue.RetryDirectory,settings.BackendStationId,rootEndpoint);}
        catch(Exception ex)
        {
            queue.TransportStatus("retry_journal_unavailable");
            log.LogWarning("WaterBench sync retry journal unavailable: {Type}",ex.GetType().Name);
            return;
        }
        using var retry=journal;
        while(!ct.IsCancellationRequested)
        {
            var delay=TimeSpan.FromSeconds(30);
            try
            {
                var endpoint=BackendBenchBatchSync.Endpoint(gateway.Value.BackendUrl);
                var secret=gateway.Value.KioskSecret;
                if(endpoint is null||endpoint!=rootEndpoint||string.IsNullOrWhiteSpace(secret)||settings.BackendStationId==Guid.Empty)
                    queue.TransportStatus("not_provisioned");
                else
                {
                    using var client=clients.CreateClient("water-bench-batch-sync");
                    var result=await BackendBenchBatchSync.SendNext(client,endpoint,secret,queue,ct,settings.EnableMediaSync,settings.EnableNh3EvidenceSync,retry);
                    if(!result.Acknowledged)queue.TransportStatus(result.State,httpStatus:result.HttpStatus);
                    delay=result.Acknowledged?TimeSpan.FromSeconds(1):
                        result.State is "idle" or "waiting_for_media" or "waiting_for_nh3" or "global_retry_wait" or "batch_retry_wait"?
                            TimeSpan.FromSeconds(10):TimeSpan.FromSeconds(1);
                }
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex)
            {
                // Never log request body, server response, URL or credential.
                queue.TransportStatus("worker_error");
                log.LogWarning("WaterBench batch sync failed: {Type}",ex.GetType().Name);
                // Unexpected persistence or programming errors require operator inspection.
                // Never fall back to a non-durable retry path.
                break;
            }
            try{await Task.Delay(delay,ct);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
        }
    }
}
