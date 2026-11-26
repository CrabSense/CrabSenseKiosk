using System.Text.Json;
using CrabSenseKiosk.Services.WaterBench;
using Xunit;
namespace CrabSenseKiosk.Tests;

public sealed class BenchCommandAuditTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
    private EvidenceSpool Open()=>new(root,1048576,100);
    private static BenchCommandAttempt Attempt()
    {
        var id=Guid.NewGuid();
        return new(1,id,Guid.NewGuid(),Guid.NewGuid(),"0123456789abcdef",1,
            new(id,"cleanStart",Mask:7),"clean start 7",DateTimeOffset.UtcNow);
    }
    private static BenchCommandOutcome Outcome(BenchCommandAttempt a,string hash,bool accepted=true)=>new(1,a.RequestId,
        a.ConnectionId,a.BootId,a.Sequence,hash,accepted?"accepted":"rejected",accepted?"accepted":"rejected",
        DateTimeOffset.UtcNow,new GatewayAck(a.BootId,a.RequestId,a.Sequence,accepted,accepted?"accepted":"rejected",200,""));
    private static JsonElement Json(object? o)=>JsonSerializer.SerializeToElement(o);
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AckBindsAttemptAndSurvivesRestart(bool accepted)
    {
        var spool=Open();var a=Attempt();var hash=spool.BeginCommand(a);
        Assert.Equal("awaiting_ack",Json(spool.CommandStatus(a.RequestId,a.RequestId)).GetProperty("state").GetString());
        spool.FinishCommand(Outcome(a,hash,accepted));
        var reopened=Open();var detail=Json(reopened.CommandStatus(a.RequestId));
        Assert.Equal(accepted?"accepted":"rejected",detail.GetProperty("state").GetString());
        Assert.False(detail.GetProperty("physicalCompletionConfirmed").GetBoolean());
        Assert.Throws<IOException>(()=>reopened.BeginCommand(a));
        Assert.Throws<IOException>(()=>reopened.FinishCommand(Outcome(a,hash,!accepted)));
    }
    [Fact]
    public void MissingOutcomeAfterRestartIsUncertainAndReservationPersists()
    {
        var spool=Open();var a=Attempt();var hash=spool.BeginCommand(a);
        for(int i=0;i<98;i++)spool.Event(new{test=i});
        Assert.Throws<IOException>(()=>spool.Event(new{test="full"}));
        var reopened=Open();
        Assert.Equal("unknown_no_replay",Json(reopened.CommandStatus(a.RequestId)).GetProperty("state").GetString());
        Assert.Equal(1,Json(reopened.CapacitySnapshot()).GetProperty("reservedCommandOutcomeFiles").GetInt32());
        reopened.FinishCommand(new(1,a.RequestId,a.ConnectionId,a.BootId,a.Sequence,hash,"unknown","disconnect",DateTimeOffset.UtcNow));
        Assert.Equal("unknown",Json(reopened.CommandStatus(a.RequestId)).GetProperty("state").GetString());
        Assert.Equal(0,Json(reopened.CapacitySnapshot()).GetProperty("reservedCommandOutcomeFiles").GetInt32());
    }
    [Fact]
    public void MismatchedAckAndTamperedAttemptNeverAppearAccepted()
    {
        var spool=Open();var a=Attempt();var hash=spool.BeginCommand(a);var o=Outcome(a,hash);
        Assert.Throws<FormatException>(()=>spool.FinishCommand(o with{Ack=o.Ack! with{Sequence=2}}));
        Assert.Throws<FormatException>(()=>spool.FinishCommand(o with{AttemptSha256=new string('0',64)}));
        spool.FinishCommand(o);
        var file=Path.Combine(root,"command-"+a.RequestId.ToString("N")+".json");
        File.AppendAllText(file," ");
        Assert.Equal("invalid_journal",Json(spool.CommandStatus(a.RequestId)).GetProperty("state").GetString());
        Assert.False(Json(spool.Commands(0,25,null)).GetProperty("inventoryComplete").GetBoolean());
    }
    [Fact]
    public void LegacyAndOrphanOutcomesAreNotCountedAsVerified()
    {
        var spool=Open();var id=Guid.NewGuid();spool.Command(id,new{state="send_attempt"});spool.Outcome(id,new{state="accepted"});
        var a=Attempt();spool.BeginCommand(a);
        File.WriteAllText(Path.Combine(root,"outcome-"+Guid.NewGuid().ToString("N")+".json"),"{}");
        var page=Json(spool.Commands(0,1,null));
        Assert.Equal(2,page.GetProperty("total").GetInt32());Assert.Equal(1,page.GetProperty("legacyUnverified").GetInt32());
        Assert.Equal(1,page.GetProperty("unknown").GetInt32());Assert.Equal(0,page.GetProperty("accepted").GetInt32());
        Assert.False(page.GetProperty("inventoryComplete").GetBoolean());
        Assert.Single(page.GetProperty("items").EnumerateArray());
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
