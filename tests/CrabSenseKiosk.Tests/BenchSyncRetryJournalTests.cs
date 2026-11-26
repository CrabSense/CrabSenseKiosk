using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CrabSenseKiosk.Services.WaterBench;
using Xunit;

namespace CrabSenseKiosk.Tests;
public sealed class BenchSyncRetryJournalTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
    private readonly Guid _station=Guid.NewGuid();
    private static readonly Uri Endpoint=new("https://example.test/api/edge/water-bench/batch-events");
    private BenchSyncRetryJournal Open()=>new(Path.Combine(_root,"retry"),_station,Endpoint);
    private BenchSyncCandidate Candidate(string batch)=>new(Guid.NewGuid(),"batch",new string('a',64),_station,
        "0123456789abcdef",batch,Guid.NewGuid(),DateTimeOffset.UtcNow,1);
    [Fact]
    public void ConflictIsIsolatedAndBackoffSurvivesRestart()
    {
        var a=Candidate("1111222233334444");var b=Candidate("5555666677778888");var now=DateTimeOffset.UtcNow;
        DateTimeOffset due;
        using(var j=Open())
        {
            j.Begin(a,now);j.Finish(a,new("backend_conflict",409),now);
            Assert.False(j.Eligible(a,now));Assert.True(j.Eligible(b,now));
            due=j.Status(now).NextRetryAt!.Value;Assert.True(due>=now.AddSeconds(120));
        }
        using(var j=Open())
        {
            Assert.False(j.Eligible(a,now));Assert.True(j.Eligible(b,now));Assert.True(j.Eligible(a,due));
            j.Begin(a,due);j.Finish(a,new("acknowledged",200,true),due);
            Assert.True(j.Eligible(a,due));Assert.Equal(0,j.Status(due).Deferred);
        }
    }
    [Fact]
    public void RateLimitAndCredentialFailurePauseEntireStation()
    {
        var a=Candidate("1111222233334444");var b=Candidate("5555666677778888");var now=DateTimeOffset.UtcNow;
        using var j=Open();j.Begin(a,now);j.Finish(a,new("backend_rate_limited",429,RetryAfterSeconds:900),now);
        Assert.False(j.Eligible(b,now.AddSeconds(899)));Assert.True(j.Eligible(b,now.AddSeconds(900)));
        now=now.AddSeconds(900);j.Begin(b,now);j.Finish(b,new("credential_rejected",401),now);
        Assert.False(j.Eligible(a,now.AddSeconds(299)));
    }
    [Fact]
    public void AttemptBeforeSendAndExclusiveLeasePersistAcrossRestart()
    {
        var a=Candidate("1111222233334444");var now=DateTimeOffset.UtcNow;
        using(var j=Open())
        {
            j.Begin(a,now);
            Assert.ThrowsAny<IOException>(()=>{using var duplicate=Open();});
        }
        using(var j=Open())
        {
            Assert.False(j.Eligible(a,now.AddSeconds(89)));Assert.True(j.Eligible(a,now.AddSeconds(90)));
            j.Begin(a,now.AddSeconds(90));j.Finish(a,new("transport_timeout"),now.AddSeconds(90));
            Assert.True(j.Status(now.AddSeconds(90)).NextRetryAt>=now.AddSeconds(150));
        }
    }
    [Fact]
    public void CorruptJournalAndChangedBackendFailClosed()
    {
        using(var j=Open()){j.Begin(Candidate("1111222233334444"),DateTimeOffset.UtcNow);}
        Assert.Throws<IOException>(()=>{using var other=new BenchSyncRetryJournal(Path.Combine(_root,"retry"),_station,new Uri("https://other.test"));});
        File.WriteAllText(Path.Combine(_root,"retry","retry.json"),"{}");
        Assert.Throws<IOException>(()=>{using var corrupt=Open();});
    }
    [Fact]
    public void RetryAfterHeaderIsBoundedAndSupportsDate()
    {
        using var r=new HttpResponseMessage(HttpStatusCode.TooManyRequests);var now=DateTimeOffset.UtcNow;
        r.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
        Assert.Equal(120,BackendBenchBatchSync.RetryAfter(r,now));
        r.Headers.RetryAfter=new RetryConditionHeaderValue(now.AddHours(48));
        Assert.Equal(86400,BackendBenchBatchSync.RetryAfter(r,now));
    }
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>send(r,ct);
    }
    [Fact]
    public async Task WorkerSkipsRejectedBatchAndAcknowledgesAnother()
    {
        var spool=new EvidenceSpool(Path.Combine(_root,"spool"),1048576,100);var now=DateTimeOffset.UtcNow;
        var a=new BenchBatchEvent(_station,Guid.NewGuid(),Guid.NewGuid(),"0123456789abcdef","1111222233334444",1,"start",now.AddSeconds(-1),Mode:"manual",Mask:1);
        var b=a with{EventId=Guid.NewGuid(),ConnectionId=Guid.NewGuid(),BatchId="5555666677778888",ReceivedAt=now};
        spool.BatchEvent(a);spool.BatchEvent(b);var q=new BenchSyncQueue(spool,_station);
        using var journal=Open();var sent=new List<Guid>();
        using var client=new HttpClient(new Handler(async(r,ct)=>{
            var e=JsonSerializer.Deserialize<BenchBatchEvent>(await r.Content!.ReadAsStringAsync(ct),BenchMediaManifest.Json)!;
            sent.Add(e.EventId);
            return e.EventId==a.EventId?new HttpResponseMessage(HttpStatusCode.Conflict):
                new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{
                    success=true,data=new{eventId=e.EventId,receiptId=Guid.NewGuid(),duplicate=false}}),Encoding.UTF8,"application/json")};
        }));
        Assert.Equal("backend_conflict",(await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",q,default,retry:journal)).State);
        Assert.True((await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",q,default,retry:journal)).Acknowledged);
        Assert.Equal(new[]{a.EventId,b.EventId},sent);Assert.Equal(1,q.Scan().Acknowledged);
        Assert.Equal("batch_retry_wait",(await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",q,default,retry:journal)).State);
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
