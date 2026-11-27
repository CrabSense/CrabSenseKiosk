using System.Text.Json;
using CrabSenseKiosk.Services.WaterBench;
using Xunit;

namespace CrabSenseKiosk.Tests;
public sealed class BenchCleaningJournalTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
    private readonly Guid station=Guid.NewGuid(),connection=Guid.NewGuid(),run=Guid.NewGuid();
    private const string Boot="0123456789abcdef";
    private EvidenceSpool Open()=>new(root,1048576,1000);
    private static DateTimeOffset Now=>DateTimeOffset.UtcNow;
    private static JsonElement Json(object o)=>JsonSerializer.SerializeToElement(o,BenchMediaManifest.Json);
    private BenchCleaningJournal Begin(EvidenceSpool spool,int mask=7)
    {
        var j=new BenchCleaningJournal(station,connection,Boot,run,mask,spool);j.Begin(Now);return j;
    }
    private static void Ack(BenchCleaningJournal j,Guid id,bool ok=true)=>j.Ack(new(Boot,id,1,ok,ok?"accepted":"rejected",123,""),Now);
    private static BenchCleanStatus S(string step,int dirty,int mask,int verified,int vial,int cycle)=>
        new(step,dirty,mask,verified,vial,cycle,true,false);
    private static void Vial(BenchCleaningJournal j,int vial,int verified,int cycles,int mask=7)
    {
        var route=new BenchCommand(Guid.NewGuid(),"cleanRoute",Id:vial);j.Command(route,Now);
        j.Observe(S("initial_drain",7,mask,verified,vial,0),Now);Ack(j,route.RequestId);
        j.Observe(S("initial_total",7,mask,verified,vial,0),Now);
        for(int c=1;c<=cycles;c++)
            foreach(var step in new[]{"source","fill","settle","drain","total"})j.Observe(S(step,7,mask,verified,vial,c),Now);
        j.Observe(S("verify",7,mask,verified,vial,cycles),Now);
        var verify=new BenchCommand(Guid.NewGuid(),"cleanVerify",Id:vial);j.Command(verify,Now);
        int next=verified|BenchCleanStatus.Bit(vial);
        j.Observe(next==mask?S("complete",7&~mask,mask,next,0,cycles):
            S("route",7,mask,next,BenchCleanStatus.First(mask&~next),0),Now);
        Ack(j,verify.RequestId);
    }
    [Fact]
    public void ThreeVialsAndMultipleCyclesWithStatusBeforeAckReplay()
    {
        var spool=Open();var j=Begin(spool);
        var first=S("route",7,7,0,1,0);j.Observe(first,Now);j.Observe(first,Now);Ack(j,run);
        Vial(j,1,0,2);Vial(j,2,1,1);Vial(j,3,3,1);
        Assert.Equal("firmware_complete_reported",j.State);Assert.False(j.Unsettled);
        var result=Json(Open().CleaningRuns(null,0,1,run));var summary=result.GetProperty("summary");
        Assert.True(result.GetProperty("inventoryComplete").GetBoolean());
        Assert.Equal("firmware_complete_reported",summary.GetProperty("state").GetString());
        Assert.Equal(7,summary.GetProperty("lastStatus").GetProperty("verifiedMask").GetInt32());
        Assert.False(summary.GetProperty("physicalCleaningVerified").GetBoolean());
        Assert.False(summary.GetProperty("chemicalResidueMeasured").GetBoolean());
        Assert.Equal(1,result.GetProperty("events").EnumerateArray().Count(x=>x.GetProperty("type").GetString()=="status"&&
            x.GetProperty("status").GetProperty("step").GetString()=="route"&&x.GetProperty("status").GetProperty("vial").GetInt32()==1));
    }
    [Fact]
    public void MissingStepAndWrongVialAreRejectedWithoutAppending()
    {
        var spool=Open();var j=Begin(spool);j.Observe(S("route",7,7,0,1,0),Now);Ack(j,run);
        Assert.Throws<FormatException>(()=>j.Command(new(Guid.NewGuid(),"cleanRoute",Id:2),Now));
        Assert.Throws<FormatException>(()=>j.Observe(S("fill",7,7,0,1,1),Now));
        j.Interrupt("invalid_or_unstored_wire_evidence",Now);
        Assert.Equal("interrupted_observed",j.State);
        Assert.Equal(4,Directory.GetFiles(root,"clean-audit-*.json").Length);
    }
    [Fact]
    public void RestartDuringCleaningNeverLooksComplete()
    {
        var spool=Open();var j=Begin(spool,1);j.Observe(S("route",7,1,0,1,0),Now);Ack(j,run);
        var result=Json(Open().CleaningRuns(null,0,1,run));
        Assert.Equal("interrupted_or_ack_unknown_no_replay",result.GetProperty("summary").GetProperty("state").GetString());
        File.Delete(Path.Combine(root,BenchCleaningInventory.Name(run,2)));
        Assert.False(Json(Open().CleaningRuns(null,0,1,run)).GetProperty("inventoryComplete").GetBoolean());
    }
    [Fact]
    public void FaultAndRejectedStartAreDistinct()
    {
        var spool=Open();var j=Begin(spool);Ack(j,run,false);Assert.Equal("start_rejected",j.State);
        var other=Guid.NewGuid();var active=new BenchCleaningJournal(station,connection,Boot,other,1,spool);
        active.Begin(Now);active.Observe(S("route",7,1,0,1,0),Now);Ack(active,other);
        active.Observe(S("fault",7,1,0,1,0),Now);Assert.Equal("fault_reported",active.State);
    }
    [Fact]
    public void FirmwareFaultWhileAwaitingAckRetainsFaultAndEndsPendingRun()
    {
        var spool=Open();var j=Begin(spool);
        j.Observe(S("route",7,7,0,1,0),Now);Ack(j,run);
        var route=new BenchCommand(Guid.NewGuid(),"cleanRoute",Id:1);j.Command(route,Now);
        j.Observe(S("fault",7,7,0,1,0),Now);
        Assert.True(j.Unsettled);
        j.Interrupt("firmware_clean_fault_ack_unknown",Now);
        Assert.False(j.Unsettled);Assert.Equal("fault_reported",j.State);
        var detail=Json(Open().CleaningRuns(null,0,1,run));
        Assert.True(detail.GetProperty("inventoryComplete").GetBoolean());
        Assert.Equal("fault_reported",detail.GetProperty("summary").GetProperty("state").GetString());
    }
    [Fact]
    public void PartialCleaningDoesNotClearUnselectedDirtyBits()
    {
        var spool=Open();var j=Begin(spool,1);j.Observe(S("route",7,1,0,1,0),Now);Ack(j,run);Vial(j,1,0,1,1);
        var summary=Json(spool.CleaningRuns(null,0,1,run)).GetProperty("summary");
        Assert.Equal(6,summary.GetProperty("lastStatus").GetProperty("dirtyMask").GetInt32());
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
