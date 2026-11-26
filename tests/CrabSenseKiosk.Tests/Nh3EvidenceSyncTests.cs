using System.Net;
using System.Text;
using System.Text.Json;
using CrabSenseKiosk.Services.WaterBench;
using Xunit;

namespace CrabSenseKiosk.Tests;
public sealed class Nh3EvidenceSyncTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
    private readonly Guid _station=Guid.NewGuid();
    private static readonly Uri Endpoint=new("https://example.test/api/edge/water-bench/batch-events");
    private (EvidenceSpool Spool,BenchSyncQueue Queue,Nh3EvidenceEvent Sample,Nh3EvidenceEvent Link) Seed()
    {
        var spool=new EvidenceSpool(_root,1048576,100);var now=DateTimeOffset.UtcNow;
        var e=new BenchBatchEvent(_station,Guid.NewGuid(),Guid.NewGuid(),"0123456789abcdef","abcdef0123456789",1,"start",now,Mode:"manual",Mask:4);
        var s=e with{EventId=Guid.NewGuid(),Type="session",Sequence=2,Analyte="NH3",SessionId="1111222233334444"};
        var r=s with{EventId=Guid.NewGuid(),Type="reaction",Sequence=3,ReactionStartMs=1000,ReactionDurationMs=600000};
        var i=s with{EventId=Guid.NewGuid(),Type="image",Sequence=4,CaptureId=7};
        foreach(var row in new[]{e,s,r,i})spool.BatchEvent(row);
        var sample=new Nh3EvidenceEvent(_station,Guid.NewGuid(),e.ConnectionId,e.BootId,e.BatchId,s.SessionId!,"sample",now,
            "000000000001","000000000002",new Nh3SampleReading(500,550,null,600,28.5m,3000));
        var link=sample with{EventId=Guid.NewGuid(),Type="image_link",Sample=null,Image=new Nh3ImageObservation(7,601500,601000)};
        spool.Nh3Event(sample);spool.Nh3Event(link);
        return(spool,new BenchSyncQueue(spool,_station),sample,link);
    }
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>send(r,ct);
    }
    [Fact]
    public async Task TimelineFirstThenNh3SampleAndImageLinkWithStableReceipt()
    {
        var(_,q,sample,link)=Seed();var sent=new List<Guid>();
        using var client=new HttpClient(new Handler(async(r,ct)=>{
            using var doc=JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));var body=doc.RootElement;
            var id=body.GetProperty("eventId").GetGuid();sent.Add(id);
            if(sent.Count<=4)Assert.Equal(Endpoint,r.RequestUri);
            else
            {
                Assert.Equal(new Uri("https://example.test/api/edge/water-bench/nh3-evidence"),r.RequestUri);
                Assert.Equal("test-secret",Assert.Single(r.Headers.GetValues("X-Kiosk-Key")));
                if(id==sample.EventId)
                {
                    Assert.Equal("sample",body.GetProperty("type").GetString());
                    Assert.Equal(JsonValueKind.Null,body.GetProperty("sample").GetProperty("ph").ValueKind);
                    Assert.Equal(28.5m,body.GetProperty("sample").GetProperty("temperatureC").GetDecimal());
                }
                else Assert.Equal(7,body.GetProperty("image").GetProperty("captureId").GetInt32());
            }
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(
                new{success=true,data=new{eventId=id,receiptId=Guid.NewGuid(),duplicate=false}}),Encoding.UTF8,"application/json")};
        }));
        Assert.False(new WaterBenchOptions().EnableNh3EvidenceSync);
        for(int n=0;n<4;n++)Assert.True((await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",q,default,true,true)).Acknowledged);
        Assert.Equal("waiting_for_nh3",(await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",q,default,true)).State);
        Assert.True((await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",q,default,true,true)).Acknowledged);
        var reopened=new BenchSyncQueue(new EvidenceSpool(_root,1048576,100),_station);
        Assert.True((await BackendBenchBatchSync.SendNext(client,Endpoint,"test-secret",reopened,default,true,true)).Acknowledged);
        Assert.Equal(new[]{sample.EventId,link.EventId},sent.Skip(4));Assert.Equal(6,reopened.Scan().Acknowledged);
    }
    [Fact]
    public void RejectWrongCaptureAndKeepMissingSensorsNull()
    {
        var(spool,q,sample,link)=Seed();
        Nh3EvidencePolicy.Validate(sample);
        Assert.Throws<FormatException>(()=>Nh3EvidencePolicy.Validate(sample with{Sample=sample.Sample! with{Ph=20}}));
        Assert.Throws<FormatException>(()=>Nh3EvidencePolicy.Validate(sample with{Sample=sample.Sample! with{TemperatureObservedUptimeMs=2000}}));
        File.WriteAllBytes(Path.Combine(_root,"nh3-evidence-"+link.EventId.ToString("N")+".json"),
            JsonSerializer.SerializeToUtf8Bytes(link with{Image=link.Image! with{CaptureId=8}},BenchMediaManifest.Json));
        Assert.Equal(1,q.Scan().BlockedBatches);Assert.Empty(q.Scan().Ready);
    }
    [Fact]
    public void WirePreservesNullAndWrapAwareObservation()
    {
        var e=WireProtocol.Json("{\"clock\":\"boot_uptime\",\"sensor_vial_alignment_verified\":false,\"salinity_ppt\":null,\"sample_uptime_ms\":4294967195,\"ph_observed_uptime_ms\":50,\"ph\":null,\"temperature_observed_uptime_ms\":100,\"temperature_c\":28.5,\"temperature_register_max_age_ms\":3000}");
        var s=Nh3EvidenceWire.Sample(e);Assert.Null(s.Ph);Assert.Equal(28.5m,s.TemperatureC);
        Assert.Equal(201u,Nh3EvidencePolicy.Elapsed(s.TemperatureObservedUptimeMs,s.SampleUptimeMs));
        Assert.Null(Nh3EvidenceWire.Estimate(s,null,"0123456789abcdef",0));
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
