using System.Text.Json;
using CrabSenseKiosk.Services.WaterBench;
using SkiaSharp;
using Xunit;

namespace CrabSenseKiosk.Tests;
public sealed class BenchSyncQueueTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
    private readonly Guid _station=Guid.NewGuid();
    private EvidenceSpool Open()=>new(_root,1048576,100);
    private BenchBatchEvent Start()=>new(_station,Guid.NewGuid(),Guid.NewGuid(),"0123456789abcdef","abcdef0123456789",1,"start",
        DateTimeOffset.UtcNow,Mode:"manual",Mask:1);
    private static BenchBatchEvent Next(BenchBatchEvent e,int seq,string type)=>e with{EventId=Guid.NewGuid(),Sequence=seq,Type=type};
    private static void Ack(BenchSyncQueue q,BenchSyncCandidate c)=>q.Accept(c,new(c.EventId,Guid.NewGuid()),DateTimeOffset.UtcNow);
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
    [Fact]
    public void RestartsFromReceiptAndDoesNotOverwriteEvidence()
    {
        var spool=Open();var e=Start();var s=Next(e,2,"session") with{Analyte="NO2",SessionId=e.BatchId+"-1"};
        spool.BatchEvent(s);spool.BatchEvent(e);var q=new BenchSyncQueue(spool,_station);
        var first=Assert.Single(q.Scan().Ready);Assert.Equal(e.EventId,first.EventId);
        Assert.Equal(e,Assert.IsType<BenchBatchEvent>(q.Prepare(first)));
        var receipt=new BenchSyncServerReceipt(e.EventId,Guid.NewGuid());
        q.Accept(first,receipt,DateTimeOffset.UtcNow);q.Accept(first,receipt,DateTimeOffset.UtcNow);
        var reopened=new BenchSyncQueue(Open(),_station);var plan=reopened.Scan();
        Assert.Equal(1,plan.Acknowledged);Assert.Equal(1,plan.Pending);
        Assert.Equal(s.EventId,Assert.Single(plan.Ready).EventId);
        Assert.True(File.Exists(Path.Combine(_root,"batch-timeline-"+e.EventId.ToString("N")+".json")));
        Assert.Throws<IOException>(()=>reopened.Accept(first,receipt with{ReceiptId=Guid.NewGuid()},DateTimeOffset.UtcNow));
    }
    [Fact]
    public void GapsAndForeignStationNeverBecomeReady()
    {
        var spool=Open();var e=Start();spool.BatchEvent(e);spool.BatchEvent(Next(e,3,"interrupted"));
        spool.BatchEvent(Start() with{StationId=Guid.NewGuid()});
        var p=new BenchSyncQueue(spool,_station).Scan();
        Assert.Equal(1,p.BlockedBatches);Assert.Equal(1,p.Foreign);Assert.Empty(p.Ready);
    }
    [Fact]
    public void CorruptReceiptOrMetadataBlocksRatherThanSkips()
    {
        var spool=Open();var e=Start();spool.BatchEvent(e);var q=new BenchSyncQueue(spool,_station);
        var c=Assert.Single(q.Scan().Ready);
        Assert.Throws<IOException>(()=>q.Accept(c,new(Guid.NewGuid(),Guid.NewGuid()),DateTimeOffset.UtcNow));
        Ack(q,c);
        File.WriteAllText(Path.Combine(_root,BenchSyncQueue.ReceiptName(e.EventId)),"{}");
        Assert.Equal("invalid_evidence",q.Scan().State);Assert.Empty(q.Scan().Ready);
    }
    [Fact]
    public void MediaWaitsForTimelineAndRevalidatesBytesBeforeTransport()
    {
        var spool=Open();var e=Start();var s=Next(e,2,"session") with{Analyte="NO2",SessionId=e.BatchId+"-1"};
        var r=Next(s,3,"reaction") with{ReactionStartMs=1,ReactionDurationMs=300000};
        var i=Next(s,4,"image") with{CaptureId=1};
        foreach(var v in new[]{e,s,r,i})spool.BatchEvent(v);
        using var bitmap=new SKBitmap(32,32);bitmap.Erase(SKColors.Beige);
        using var image=SKImage.FromBitmap(bitmap);using var encoded=image.Encode(SKEncodedImageFormat.Jpeg,90);
        var bytes=encoded.ToArray();
        var id=spool.Frame(new ReceivedFrame(1,2,32,32,WireProtocol.Crc32(bytes),bytes,i.ReceivedAt),new{test=true},
            new BenchMediaIdentity(_station,e.ConnectionId,e.BootId,e.BatchId,"NO2",s.SessionId!,i.ReceivedAt,"000000000001","000000000002"));
        var q=new BenchSyncQueue(spool,_station);
        for(int n=1;n<=4;n++)
        {
            var next=Assert.Single(q.Scan().Ready);Assert.Equal("batch",next.Kind);Assert.Equal(n,next.Sequence);Ack(q,next);
        }
        var m=Assert.Single(q.Scan().Ready);Assert.Equal(Guid.ParseExact(id,"N"),m.EventId);
        var body=JsonSerializer.SerializeToElement(q.Prepare(m),BenchMediaManifest.Json);
        Assert.Equal(Convert.ToBase64String(bytes),body.GetProperty("jpegBase64").GetString());
        var terminal=Next(e,5,"interrupted");spool.BatchEvent(terminal);
        var receipt=new BenchSyncServerReceipt(m.EventId,Guid.NewGuid(),Guid.NewGuid(),m.ImageSha256);
        q.Accept(m,receipt,DateTimeOffset.UtcNow); // valid in-flight media acknowledgment
        Ack(q,Assert.Single(q.Scan().Ready));
        Assert.Equal(0,q.Scan().Pending);Assert.Equal(6,q.Scan().Acknowledged);
    }
    [Fact]
    public void SpoolReservesReceiptSlotsEvenWhenRawEvidenceFillsCapacity()
    {
        var spool=Open();var e=Start();spool.BatchEvent(e);
        for(int n=0;n<98;n++)spool.Event(new{n});
        Assert.Throws<IOException>(()=>spool.Event(new{overflow=true}));
        var q=new BenchSyncQueue(spool,_station);Ack(q,Assert.Single(q.Scan().Ready));
        Assert.Equal(100,Directory.GetFiles(_root).Length);Assert.False(spool.HasCapacity);
        Assert.Equal(1,new BenchSyncQueue(Open(),_station).Scan().Acknowledged);
    }
}
