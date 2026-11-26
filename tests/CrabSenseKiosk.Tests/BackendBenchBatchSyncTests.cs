using System.Net;
using System.Text;
using System.Text.Json;
using CrabSenseKiosk.Services.WaterBench;
using Xunit;

namespace CrabSenseKiosk.Tests;
public sealed class BackendBenchBatchSyncTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
    private static readonly Uri Endpoint=new("https://example.test/api/edge/water-bench/batch-events");
    private readonly Guid _station=Guid.NewGuid();
    private (BenchSyncQueue Queue,BenchBatchEvent First,BenchBatchEvent Second) Seed()
    {
        var spool=new EvidenceSpool(_root,1048576,100);
        var e=new BenchBatchEvent(_station,Guid.NewGuid(),Guid.NewGuid(),"0123456789abcdef","abcdef0123456789",1,"start",
            DateTimeOffset.UtcNow,Mode:"manual",Mask:1);
        var s=e with{EventId=Guid.NewGuid(),Sequence=2,Type="session",Analyte="NO2",SessionId=e.BatchId+"-1"};
        spool.BatchEvent(s);spool.BatchEvent(e);
        return(new(spool,_station),e,s);
    }
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>send(r,ct);
    }
    private static HttpResponseMessage Response(Guid id,Guid receipt,bool duplicate=false)=>new(HttpStatusCode.OK)
    {
        Content=new StringContent(JsonSerializer.Serialize(new{success=true,data=new{eventId=id,receiptId=receipt,duplicate}}),
            Encoding.UTF8,"application/json")
    };
    [Theory]
    [InlineData("http://example.test")]
    [InlineData("https://user:pass@example.test")]
    [InlineData("https://example.test/?q=x")]
    [InlineData("https://example.test/other")]
    public void RejectUnsafeEndpoint(string url)=>Assert.Null(BackendBenchBatchSync.Endpoint(url));
    [Fact]
    public void SupportsRootAndApiPrefixAndDefaultsDisabled()
    {
        Assert.Equal(Endpoint,BackendBenchBatchSync.Endpoint("https://example.test"));
        Assert.Equal(Endpoint,BackendBenchBatchSync.Endpoint("https://example.test/api"));
        Assert.False(new WaterBenchOptions().EnableBatchSync);
    }
    [Fact]
    public async Task SendsOrderedEventsAndPersistsServerReceipt()
    {
        var(q,e,s)=Seed();var sent=new List<Guid>();var receipt=Guid.NewGuid();
        using var client=new HttpClient(new Handler(async(r,ct)=>{
            Assert.Equal(HttpMethod.Post,r.Method);Assert.Equal(Endpoint,r.RequestUri);
            Assert.Equal("test-secret",Assert.Single(r.Headers.GetValues("X-Kiosk-Key")));
            var body=JsonSerializer.Deserialize<BenchBatchEvent>(await r.Content!.ReadAsStringAsync(ct),BenchMediaManifest.Json)!;
            sent.Add(body.EventId);Assert.Equal(_station,body.StationId);
            return Response(body.EventId,sent.Count==1?receipt:Guid.NewGuid());
        }));
        Assert.True((await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",q,default)).Acknowledged);
        var reopened=new BenchSyncQueue(new EvidenceSpool(_root,1048576,100),_station);
        Assert.True((await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",reopened,default)).Acknowledged);
        Assert.Equal(new[]{e.EventId,s.EventId},sent);Assert.Equal(2,reopened.Scan().Acknowledged);
        Assert.Equal("idle",(await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",reopened,default)).State);
        Assert.Equal(2,sent.Count);
    }
    [Fact]
    public async Task LostResponseRetriesSameEventAndDuplicateReceiptIsAccepted()
    {
        var(q,e,_)=Seed();int attempts=0;var receipt=Guid.NewGuid();string? original=null;
        using var client=new HttpClient(new Handler(async(r,ct)=>{
            string body=await r.Content!.ReadAsStringAsync(ct);
            if(++attempts==1){original=body;throw new HttpRequestException("lost response");}
            Assert.Equal(original,body);return Response(e.EventId,receipt,true);
        }));
        Assert.Equal("transport_unavailable",(await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",q,default)).State);
        Assert.Equal(0,q.Scan().Acknowledged);
        Assert.True((await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",q,default)).Acknowledged);
        Assert.Equal(1,q.Scan().Acknowledged);
    }
    [Theory]
    [InlineData(401,"credential_rejected")]
    [InlineData(403,"credential_rejected")]
    [InlineData(409,"backend_conflict")]
    [InlineData(429,"backend_rate_limited")]
    [InlineData(503,"backend_unavailable")]
    [InlineData(302,"backend_rejected")]
    public async Task RejectedResponseNeverAdvancesQueue(int status,string state)
    {
        var(q,e,_)=Seed();
        using var client=new HttpClient(new Handler((r,ct)=>Task.FromResult(new HttpResponseMessage((HttpStatusCode)status))));
        var result=await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",q,default);
        Assert.Equal(state,result.State);Assert.Equal(status,result.HttpStatus);
        Assert.Equal(e.EventId,Assert.Single(q.Scan().Ready).EventId);Assert.Equal(0,q.Scan().Acknowledged);
    }
    [Fact]
    public async Task WrongEventAndEmptyReceiptDoNotAcknowledge()
    {
        var(q,e,_)=Seed();int attempts=0;
        using var client=new HttpClient(new Handler((r,ct)=>Task.FromResult(++attempts==1?
            Response(Guid.NewGuid(),Guid.NewGuid()):Response(e.EventId,Guid.Empty))));
        for(int n=0;n<2;n++)Assert.Equal("invalid_receipt",(await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",q,default)).State);
        Assert.Equal(0,q.Scan().Acknowledged);
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
