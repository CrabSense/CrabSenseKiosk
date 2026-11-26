using System.Net;
using System.Text;
using System.Text.Json;
using CrabSenseKiosk.Services.WaterBench;
using SkiaSharp;
using Xunit;

namespace CrabSenseKiosk.Tests;
public sealed class BackendBenchMediaSyncTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
    private readonly Guid _station=Guid.NewGuid();
    private static readonly Uri Endpoint=new("https://example.test/api/edge/water-bench/batch-events");
    private static readonly Uri MediaEndpoint=new("https://example.test/api/edge/water-bench/media");
    private (BenchSyncQueue Queue,Guid ImageId,byte[] Bytes) Seed(bool ackTimeline)
    {
        var spool=new EvidenceSpool(_root,1048576,100);
        var e=new BenchBatchEvent(_station,Guid.NewGuid(),Guid.NewGuid(),"0123456789abcdef","abcdef0123456789",1,"start",
            DateTimeOffset.UtcNow,Mode:"manual",Mask:1);
        var s=e with{EventId=Guid.NewGuid(),Sequence=2,Type="session",Analyte="NO2",SessionId=e.BatchId+"-1"};
        var r=s with{EventId=Guid.NewGuid(),Sequence=3,Type="reaction",ReactionStartMs=1,ReactionDurationMs=300000};
        var i=s with{EventId=Guid.NewGuid(),Sequence=4,Type="image",CaptureId=1};
        foreach(var v in new[]{e,s,r,i})spool.BatchEvent(v);
        using var bitmap=new SKBitmap(32,32);bitmap.Erase(SKColors.Beige);
        using var image=SKImage.FromBitmap(bitmap);using var encoded=image.Encode(SKEncodedImageFormat.Jpeg,90);
        var bytes=encoded.ToArray();
        var id=spool.Frame(new ReceivedFrame(1,2,32,32,WireProtocol.Crc32(bytes),bytes,i.ReceivedAt),new{test=true},
            new BenchMediaIdentity(_station,e.ConnectionId,e.BootId,e.BatchId,"NO2",s.SessionId!,i.ReceivedAt,"000000000001","000000000002"));
        var q=new BenchSyncQueue(spool,_station);
        if(ackTimeline)
            for(int n=0;n<4;n++)
            {
                var next=Assert.Single(q.Scan().Ready);Assert.Equal("batch",next.Kind);
                q.Accept(next,new(next.EventId,Guid.NewGuid()),DateTimeOffset.UtcNow);
            }
        return(q,Guid.ParseExact(id,"N"),bytes);
    }
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>send(r,ct);
    }
    private static HttpResponseMessage Response(object data)=>new(HttpStatusCode.OK)
    {
        Content=new StringContent(JsonSerializer.Serialize(new{success=true,data}),Encoding.UTF8,"application/json")
    };
    [Fact]
    public async Task MediaDisabledByDefaultAndTimelineAlwaysFirst()
    {
        var(q,id,bytes)=Seed(false);
        Assert.False(new WaterBenchOptions().EnableMediaSync);
        int calls=0;
        using var client=new HttpClient(new Handler(async(r,ct)=>{
            calls++;Assert.Equal(Endpoint,r.RequestUri);
            var body=JsonSerializer.Deserialize<BenchBatchEvent>(await r.Content!.ReadAsStringAsync(ct),BenchMediaManifest.Json)!;
            return Response(new{eventId=body.EventId,receiptId=Guid.NewGuid(),duplicate=false});
        }));
        for(int n=0;n<4;n++)Assert.True((await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",q,default,true)).Acknowledged);
        Assert.Equal("waiting_for_media",(await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",q,default)).State);
        Assert.Equal(4,calls);Assert.Equal(id,Assert.Single(q.Scan().Ready).EventId);
    }
    [Fact]
    public async Task UploadUsesOriginalBytesAndPersistsValidatedMediaReceiptAcrossRestart()
    {
        var(q,id,bytes)=Seed(true);var next=Assert.Single(q.Scan().Ready);
        var receiptId=Guid.NewGuid();var mediaId=Guid.NewGuid();
        using var client=new HttpClient(new Handler(async(r,ct)=>{
            Assert.Equal(MediaEndpoint,r.RequestUri);Assert.Equal(HttpMethod.Post,r.Method);
            Assert.Equal("test-secret",Assert.Single(r.Headers.GetValues("X-Kiosk-Key")));
            using var doc=JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));var body=doc.RootElement;
            Assert.Equal(id,body.GetProperty("eventId").GetGuid());Assert.Equal(_station,body.GetProperty("stationId").GetGuid());
            Assert.Equal("NO2",body.GetProperty("analyte").GetString());Assert.Equal(1,body.GetProperty("captureId").GetInt32());
            Assert.Equal(next.ImageSha256,body.GetProperty("sha256").GetString());
            Assert.Equal(Convert.ToBase64String(bytes),body.GetProperty("jpegBase64").GetString());
            return Response(new{eventId=id,receiptId,mediaId,sha256=next.ImageSha256,duplicate=false});
        }));
        Assert.True((await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",q,default,true)).Acknowledged);
        var reopened=new BenchSyncQueue(new EvidenceSpool(_root,1048576,100),_station);
        Assert.Equal(5,reopened.Scan().Acknowledged);Assert.Empty(reopened.Scan().Ready);
        var saved=JsonSerializer.Deserialize<BenchSyncReceipt>(File.ReadAllText(Path.Combine(_root,BenchSyncQueue.ReceiptName(id))),BenchMediaManifest.Json)!;
        Assert.Equal(mediaId,saved.MediaId);Assert.Equal(receiptId,saved.ReceiptId);Assert.Equal(next.ImageSha256,saved.Sha256);
        Assert.True(File.Exists(Path.Combine(_root,id.ToString("N")+".jpg")));
    }
    [Fact]
    public async Task LostMediaResponseRetriesExactPayloadAndAcceptsDuplicate()
    {
        var(q,id,_)=Seed(true);var next=Assert.Single(q.Scan().Ready);
        int count=0;string? first=null;var receiptId=Guid.NewGuid();var mediaId=Guid.NewGuid();
        using var client=new HttpClient(new Handler(async(r,ct)=>{
            string body=await r.Content!.ReadAsStringAsync(ct);
            if(++count==1){first=body;throw new HttpRequestException("lost response");}
            Assert.Equal(first,body);
            return Response(new{eventId=id,receiptId,mediaId,sha256=next.ImageSha256,duplicate=true});
        }));
        Assert.Equal("transport_unavailable",(await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",q,default,true)).State);
        Assert.Equal(1,q.Scan().Pending);
        Assert.True((await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",q,default,true)).Acknowledged);
        Assert.Equal(0,q.Scan().Pending);
    }
    [Theory]
    [InlineData("event")]
    [InlineData("receipt")]
    [InlineData("media")]
    [InlineData("hash")]
    public async Task WrongMediaReceiptNeverAdvancesQueue(string field)
    {
        var(q,id,_)=Seed(true);var next=Assert.Single(q.Scan().Ready);
        using var client=new HttpClient(new Handler((r,ct)=>Task.FromResult(Response(new{
            eventId=field=="event"?Guid.NewGuid():id,receiptId=field=="receipt"?Guid.Empty:Guid.NewGuid(),
            mediaId=field=="media"?Guid.Empty:Guid.NewGuid(),sha256=field=="hash"?new string('0',64):next.ImageSha256,duplicate=false
        }))));
        Assert.Equal("invalid_receipt",(await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",q,default,true)).State);
        Assert.Equal(1,q.Scan().Pending);
    }
    [Fact]
    public async Task ChangedJpegIsNeverSent()
    {
        var(q,id,bytes)=Seed(true);
        bytes[bytes.Length/2]^=1;File.WriteAllBytes(Path.Combine(_root,id.ToString("N")+".jpg"),bytes);
        using var client=new HttpClient(new Handler((r,ct)=>throw new InvalidOperationException("unexpected request")));
        Assert.Equal("local_evidence_or_receipt_error",(await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",q,default,true)).State);
        Assert.Equal(1,q.Scan().Pending);
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
