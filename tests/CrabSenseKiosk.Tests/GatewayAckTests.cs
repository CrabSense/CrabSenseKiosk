using CrabSenseKiosk.Services.WaterBench;
using Xunit;
namespace CrabSenseKiosk.Tests;
public sealed class GatewayAckTests
{
    [Fact]
    public void ValidatesNonceBootSequenceAndResult()
    {
        var id=Guid.NewGuid();const string boot="0123456789abcdef";
        var hello=GatewayWire.Hello(WireProtocol.Json($"{{\"version\":1,\"nonce\":\"{id:N}\",\"boot_id\":\"{boot}\",\"last_seq\":2,\"uptime_ms\":100}}"),id);
        Assert.Equal(2u,hello.LastSequence);
        Assert.Throws<FormatException>(()=>GatewayWire.Hello(WireProtocol.Json($"{{\"version\":1,\"nonce\":\"{id:N}\",\"boot_id\":\"{boot}\",\"last_seq\":2,\"uptime_ms\":100}}"),Guid.NewGuid()));
        Assert.Equal($"gw {boot} 3 {id:N} batch start scheduled",GatewayWire.Command(boot,3,id,"batch start scheduled"));
        Assert.Throws<FormatException>(()=>GatewayWire.Command(boot,3,id,"batch reset\nstop"));
        var ack=GatewayWire.Ack(WireProtocol.Json($"{{\"version\":1,\"boot_id\":\"{boot}\",\"request_id\":\"{id:N}\",\"seq\":3,\"accepted\":true,\"reason\":\"accepted\",\"uptime_ms\":101,\"batch_id\":\"{boot}\"}}"));
        Assert.Equal(id,ack.RequestId);Assert.True(ack.Accepted);
        Assert.Throws<FormatException>(()=>GatewayWire.Ack(WireProtocol.Json($"{{\"version\":1,\"boot_id\":\"{boot}\",\"request_id\":\"{id:N}\",\"seq\":0,\"accepted\":true,\"reason\":\"accepted\",\"uptime_ms\":101,\"batch_id\":\"\"}}")));
    }
    [Fact]
    public void ClockRolloverAndBoundedEstimate()
    {
        var t=new DateTimeOffset(2026,11,26,0,0,0,TimeSpan.Zero);
        var c=new ClockObservation("0123456789abcdef",uint.MaxValue-100,t,40,1000);
        Assert.Equal(t.AddMilliseconds(101),c.Estimate(0,1101));
        Assert.Equal(t.AddMilliseconds(-100),c.Estimate(uint.MaxValue-200,1101));
        Assert.Null(c.Estimate(0,1801001));
        Assert.Null(c.Estimate(2000000,1101));
    }
    [Fact]
    public void PersistentCommandJournalNeverOverwrites()
    {
        string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try{
            var id=Guid.NewGuid();var spool=new EvidenceSpool(root,1048576,100);
            spool.Command(id,new {state="send_attempt"});
            Assert.True(spool.HasCommand(id));
            spool.Outcome(id,new {state="accepted"});
            Assert.NotNull(new EvidenceSpool(root,1048576,100).CommandStatus(id));
            Assert.Throws<IOException>(()=>spool.Outcome(id,new {state="rejected"}));
        }finally{Directory.Delete(root,true);}
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void RejectsLegacyOrUnknownGatewayVersion(int version)
    {
        var id=Guid.NewGuid();
        const string boot="0123456789abcdef";
        var hello=System.Text.Json.JsonSerializer.Serialize(new {version,nonce=id.ToString("N"),boot_id=boot,last_seq=0,uptime_ms=10});
        var ack=System.Text.Json.JsonSerializer.Serialize(new {version,boot_id=boot,request_id=id.ToString("N"),seq=1,accepted=true,reason="accepted",uptime_ms=11,batch_id=boot});
        Assert.Throws<FormatException>(()=>GatewayWire.Hello(WireProtocol.Json(hello),id));
        Assert.Throws<FormatException>(()=>GatewayWire.Ack(WireProtocol.Json(ack)));
    }

    [Theory]
    [InlineData("boot_mismatch")]
    [InlineData("sequence_mismatch")]
    [InlineData("unsupported")]
    [InlineData("rejected")]
    public void FirmwareRejectionIsNeverAnAcceptedCommand(string reason)
    {
        var id=Guid.NewGuid();
        const string boot="0123456789abcdef";
        var json=System.Text.Json.JsonSerializer.Serialize(new {version=1,boot_id=boot,request_id=id.ToString("N"),seq=1,accepted=false,reason,uptime_ms=11,batch_id=""});
        var ack=GatewayWire.Ack(WireProtocol.Json(json));
        Assert.False(ack.Accepted);
        Assert.Equal(reason,ack.Reason);
        Assert.Equal(id,ack.RequestId);
        var inconsistent=System.Text.Json.JsonSerializer.Serialize(new {version=1,boot_id=boot,request_id=id.ToString("N"),seq=1,accepted=true,reason,uptime_ms=11,batch_id=""});
        Assert.Throws<FormatException>(()=>GatewayWire.Ack(WireProtocol.Json(inconsistent)));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("pulse")]
    [InlineData("arm")]
    [InlineData("capture")]
    [InlineData("no2 start")]
    [InlineData("dose")]
    public void LegacyAndRawActuationActionsAreNotGatewayCommands(string action)
    {
        Assert.Null(WaterBenchSession.BuildCommand(new BenchCommand(Guid.NewGuid(),action,Id:1,Mask:1,CaptureId:1)));
    }
}
