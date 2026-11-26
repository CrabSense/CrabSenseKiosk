using System.Text.Json;
using CrabSenseKiosk.Services.WaterBench;
using Xunit;

namespace CrabSenseKiosk.Tests;
public sealed class BenchSyncMonitoringTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
    private readonly Guid _station=Guid.NewGuid();
    private EvidenceSpool Open()=>new(_root,1048576,100);
    private BenchBatchEvent Start()=>new(_station,Guid.NewGuid(),Guid.NewGuid(),"0123456789abcdef","abcdef0123456789",1,"start",
        DateTimeOffset.UtcNow,Mode:"manual",Mask:4);
    private static JsonElement Json(object obj)=>JsonSerializer.SerializeToElement(obj,BenchMediaManifest.Json);
    [Fact]
    public void DurableCountsAndReceiptDetailsSurviveRestart()
    {
        var spool=Open();var e=Start();spool.BatchEvent(e);var q=new BenchSyncQueue(spool,_station);
        var c=Assert.Single(q.Scan().Ready);var receipt=Guid.NewGuid();q.Accept(c,new(c.EventId,receipt),DateTimeOffset.UtcNow);
        var reopened=new BenchSyncQueue(Open(),_station);var status=Json(reopened.Snapshot());
        Assert.Equal(1,status.GetProperty("timeline").GetProperty("acknowledged").GetInt32());
        Assert.NotEqual(JsonValueKind.Null,status.GetProperty("durableLastReceiptAt").ValueKind);
        Assert.Equal(0,status.GetProperty("storage").GetProperty("reservedReceiptFiles").GetInt32());
        var page=Json(reopened.Batches(0,25));Assert.Equal(1,page.GetProperty("total").GetInt32());
        var detail=Json(reopened.BatchReceipts(e.BootId,e.BatchId)!);
        Assert.Equal("local_receipts_complete",detail.GetProperty("summary").GetProperty("state").GetString());
        Assert.False(detail.GetProperty("summary").GetProperty("timelineTerminal").GetBoolean());
        var item=Assert.Single(detail.GetProperty("items").EnumerateArray());
        Assert.Equal(receipt,item.GetProperty("receiptId").GetGuid());Assert.Equal(c.LocalSha256,item.GetProperty("localSha256").GetString());
    }
    [Fact]
    public void RejectReceiptIdReuseAndReportCorruptInventory()
    {
        var spool=Open();var e=Start();var s=e with{EventId=Guid.NewGuid(),Sequence=2,Type="session",Analyte="NH3",SessionId="1111222233334444"};
        spool.BatchEvent(e);spool.BatchEvent(s);var q=new BenchSyncQueue(spool,_station);
        var first=Assert.Single(q.Scan().Ready);var receipt=Guid.NewGuid();q.Accept(first,new(first.EventId,receipt),DateTimeOffset.UtcNow);
        var second=Assert.Single(q.Scan().Ready);
        Assert.Throws<IOException>(()=>q.Accept(second,new(second.EventId,receipt),DateTimeOffset.UtcNow));
        File.WriteAllText(Path.Combine(_root,BenchSyncQueue.ReceiptName(first.EventId)),"{}");
        Assert.False(Json(q.Batches(0,25)).GetProperty("inventoryComplete").GetBoolean());
        Assert.Empty(q.Scan().Ready);
    }
    [Fact]
    public void Nh3InflightReceiptRequiresExactAcknowledgedSessionDespiteOtherBlockedBatch()
    {
        var spool=Open();var e=Start();var s=e with{EventId=Guid.NewGuid(),Sequence=2,Type="session",Analyte="NH3",SessionId="1111222233334444"};
        spool.BatchEvent(e);spool.BatchEvent(s);
        var sample=new Nh3EvidenceEvent(_station,Guid.NewGuid(),e.ConnectionId,e.BootId,e.BatchId,s.SessionId!,"sample",e.ReceivedAt,
            "000000000001","000000000002",new Nh3SampleReading(500,550,null,600,28.5m,3000));
        spool.Nh3Event(sample);
        var q=new BenchSyncQueue(spool,_station);
        for(int n=0;n<2;n++){var c=Assert.Single(q.Scan().Ready);q.Accept(c,new(c.EventId,Guid.NewGuid()),DateTimeOffset.UtcNow);}
        var pending=Assert.Single(q.Scan().Ready);Assert.Equal(sample.EventId,pending.EventId);
        spool.BatchEvent(s with{EventId=Guid.NewGuid(),Sequence=3,Type="reaction",ReactionStartMs=1000,ReactionDurationMs=600000});
        spool.BatchEvent(e with{EventId=Guid.NewGuid(),BatchId="2222333344445555",Sequence=3,Type="interrupted"});
        Assert.Equal(1,q.Scan().BlockedBatches);
        q.Accept(pending,new(pending.EventId,Guid.NewGuid()),DateTimeOffset.UtcNow);
        Assert.Equal(3,q.Scan().Acknowledged);
    }
    [Fact]
    public void ForeignEntriesAreNotExposedAndRetryIsReportedPerBatch()
    {
        var spool=Open();var e=Start();spool.BatchEvent(e);spool.BatchEvent(Start() with{StationId=Guid.NewGuid()});
        var q=new BenchSyncQueue(spool,_station);var c=Assert.Single(q.Scan().Ready);var due=DateTimeOffset.UtcNow.AddMinutes(2);
        q.RetryStatus(1,due,null,[new(c.BootId,c.BatchId,c.EventId,c.Kind,c.LocalSha256,2,due,"backend_conflict")]);
        var page=Json(q.Batches(0,25));var row=Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("batch_retry_wait",row.GetProperty("state").GetString());
        Assert.Equal(2,row.GetProperty("retry").GetProperty("attempts").GetInt32());
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
