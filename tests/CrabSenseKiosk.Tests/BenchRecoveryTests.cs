using System.Text.Json;
using CrabSenseKiosk.Services.WaterBench;
using Xunit;

namespace CrabSenseKiosk.Tests;
public sealed class BenchRecoveryTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
    private readonly WaterBenchOptions _options=new(){Enabled=true,EnableSupervisedControl=true,
        MasterHardwareId="000000000001",SlaveHardwareId="000000000002"};
    private EvidenceSpool Open()=>new(_root,1048576,100);
    private static JsonElement Status(WaterBenchSession s)=>JsonSerializer.SerializeToElement(s.Snapshot());
    private void Identify(WaterBenchSession s)
    {
        foreach(bool master in new[]{true,false})
        {
            var ports=Enumerable.Range(0,master?21:11).Select(i=>new{
                portId=i==0?(master?"reserve-vial-drain":"reserve-dosing"):"port-"+i,
                discovery="declared",remoteControl=false,assigned=i!=0});
            s.Feed(master,"INVENTORY "+JsonSerializer.Serialize(new{schemaVersion=1,revision=2,
                role=master?"master":"slave",board=master?"EITEK-WERS3-8IO-4AI":"ESP32-S3-N16R8",
                hardwareId=master?_options.MasterHardwareId:_options.SlaveHardwareId,
                outputsEnabled=true,rs485Enabled=true,cameraSupported=true,ports}));
        }
    }
    private static void Fresh(WaterBenchSession s,int dirty=0,int state=0,string local="0")
    {
        s.Feed(true,"BATCH STATUS "+JsonSerializer.Serialize(new{batch_id="",state,mask=0,work=0,owner=0,
            started=0,reacting=0,imaged=0,drained=0}));
        s.Feed(true,"CLEAN STATUS "+JsonSerializer.Serialize(new{step="idle",dirty_mask=dirty,selected_mask=0,
            verified_mask=0,vial=0,cycle=0,storage_ok=true,ready_for_assay=dirty==0}));
        s.Feed(true,$"link=1 local={local} armed=0 slave_state=0 slave_ch=0 cal_mask=0x7f dirty_mask={dirty} storage_ok=1");
    }
    private static void Handshake(WaterBenchSession s,List<string> writes)
    {
        s.Maintain();var hello=writes.Last();Assert.StartsWith("gw hello ",hello);
        s.Feed(true,"GW HELLO "+JsonSerializer.Serialize(new{version=1,nonce=hello["gw hello ".Length..],
            boot_id="0123456789abcdef",last_seq=0,uptime_ms=100}));
    }
    [Fact]
    public void EveryRestartRequiresFreshLocalInspectionAndNeverReplays()
    {
        var writes=new List<string>();var s=new WaterBenchSession(_options,Open());s.Connected(writes.Add);Identify(s);
        Assert.True(Status(s).GetProperty("recoveryRequired").GetBoolean());
        s.Maintain();Assert.Empty(writes);
        Assert.False(s.TryCommand(new(Guid.NewGuid(),"manual",Mask:7),out _));
        Assert.True(s.TryCommand(new(Guid.NewGuid(),"batchStatus"),out _));Assert.Equal("batch status",writes.Last());
        var confirm=new BenchRecoveryRequest(Guid.NewGuid(),true,"Inspected isolated bench");
        Assert.False(s.TryRecover(confirm,out _));Fresh(s);
        Assert.False(s.TryRecover(confirm with{InspectionConfirmed=false},out _));
        Assert.True(s.TryRecover(confirm,out _));Assert.Single(Directory.GetFiles(_root,"recovery-*.json"));
        Assert.False(Status(s).GetProperty("gatewayReady").GetBoolean());
        Assert.False(s.TryCommand(new(Guid.NewGuid(),"manual",Mask:7),out _));
        Handshake(s,writes);Assert.False(s.TryCommand(new(Guid.NewGuid(),"manual",Mask:7),out _));
        Fresh(s);Assert.True(s.TryCommand(new(Guid.NewGuid(),"manual",Mask:7),out _));
        Assert.StartsWith("gw ",writes.Last());
        var reopened=new WaterBenchSession(_options,Open());var later=new List<string>();reopened.Connected(later.Add);Identify(reopened);
        Assert.False(reopened.TryCommand(new(Guid.NewGuid(),"manual",Mask:7),out _));
        Assert.Empty(later);
        Fresh(reopened);Assert.False(reopened.TryRecover(confirm,out _)); // immutable recovery request ID
        Assert.True(Status(reopened).GetProperty("recoveryRequired").GetBoolean());
    }
    [Fact]
    public void DirtyBenchCanRecoverForSupervisedCleaningButCannotStartAssay()
    {
        var s=new WaterBenchSession(_options,Open());var writes=new List<string>();s.Connected(writes.Add);Identify(s);
        Fresh(s,dirty:7,local:"1");var request=new BenchRecoveryRequest(Guid.NewGuid(),true,"Inspected outputs and waste path");
        Assert.False(s.TryRecover(request,out _));
        Fresh(s,dirty:7);Assert.True(s.TryRecover(request,out _));Handshake(s,writes);Fresh(s,dirty:7);
        Assert.False(s.TryCommand(new(Guid.NewGuid(),"manual",Mask:1),out _));
        Assert.False(s.TryCommand(new(Guid.NewGuid(),"scheduled",Mask:7),out _));
    }
    [Fact]
    public void StopLocksRecoveryAndStillWritesWithFullEvidenceSpool()
    {
        var spool=Open();var s=new WaterBenchSession(_options,spool);var writes=new List<string>();s.Connected(writes.Add);
        Identify(s);Fresh(s);
        while(spool.HasCapacity)spool.Event(new{padding=new string('a',4096)});
        var request=new BenchCommand(Guid.NewGuid(),"stop");
        Assert.True(s.TryCommand(request,out _));Assert.Equal("stop",writes.Last());
        Assert.True(s.TryCommand(request,out _));Assert.Equal(2,writes.Count(x=>x=="stop"));
        Assert.True(Status(s).GetProperty("recoveryRequired").GetBoolean());
        Assert.False(s.TryRecover(new(Guid.NewGuid(),true,"Inspected"),out _));
    }
    [Fact]
    public void StopTransportFailureAndDisconnectRemainLocked()
    {
        var s=new WaterBenchSession(_options,Open());s.Connected(_=>throw new IOException("serial failed"));
        Assert.False(s.TryCommand(new(Guid.NewGuid(),"stop"),out var message));
        Assert.Contains("physical emergency stop",message);
        s.Disconnected();Assert.True(Status(s).GetProperty("recoveryRequired").GetBoolean());
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
