using System.Text.Json;
using CrabSenseKiosk.Services.WaterBench;
using Xunit;

namespace CrabSenseKiosk.Tests;
public sealed class BackendBenchConfigurationTests
{
    [Fact]
    public void SyncRequiresHttpsAndExplicitStation()
    {
        Assert.Null(BackendBenchConfigurationWorker.Endpoint("http://localhost:5080"));
        Assert.Null(BackendBenchConfigurationWorker.Endpoint("https://user:pass@example.test"));
        Assert.Equal("https://example.test/api/edge/water-bench/configuration",BackendBenchConfigurationWorker.Endpoint("https://example.test")!.AbsoluteUri);
    }
    [Fact]
    public void RemoteVersionCannotRollBackOrChangeWithoutRevision()
    {
        string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            var opts=new WaterBenchOptions{Enabled=true,UseBackendSchedule=true,BackendStationId=Guid.NewGuid(),
                MasterHardwareId="000000000001",SlaveHardwareId="000000000002"};
            var session=new WaterBenchSession(opts,new EvidenceSpool(Path.Combine(root,"spool"),1048576,100));
            var now=new DateTimeOffset(2026,11,26,0,0,0,TimeSpan.Zero);
            var config=new BackendBenchConfiguration(opts.BackendStationId,opts.MasterHardwareId,opts.SlaveHardwareId,2,true,true,true,
                new[]{"07:10"},5,new[]{new BackendBenchTarget("NO2",Guid.NewGuid()),new BackendBenchTarget("NO3",Guid.NewGuid()),new BackendBenchTarget("NH3",Guid.NewGuid())},
                "RAS_RETURN","UTC+07:00","kiosk_supervised",false);
            static JsonElement View(BenchSchedule s,DateTimeOffset t)=>JsonSerializer.SerializeToElement(s.Snapshot(t),new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using(var schedule=new BenchSchedule(opts,session,root,now))
            {
                Assert.False(schedule.Configure(new(true,new[]{"07:10"}),now,out _));
                Assert.False(View(schedule,now).GetProperty("enabled").GetBoolean());
                Assert.True(schedule.ApplyBackend(config,now));
                Assert.True(View(schedule,now).GetProperty("enabled").GetBoolean());
                schedule.Tick(now.AddMinutes(10));
                var id=View(schedule,now).GetProperty("lastSlot").GetProperty("id").GetGuid();
                schedule.BackendUnavailable("test");
                Assert.False(schedule.Approve(id,now.AddMinutes(10),out _));
                Assert.False(schedule.ApplyBackend(config with {ConfigurationVersion=1},now));
                Assert.False(schedule.ApplyBackend(config with {Times=new[]{"08:00"}},now));
                Assert.True(schedule.ApplyBackend(config with {Enabled=false,ProfilesReady=false},now.AddMinutes(11)));
                Assert.False(View(schedule,now).GetProperty("enabled").GetBoolean());
            }
            using(var schedule=new BenchSchedule(opts,session,root,now.AddMinutes(12)))
            {
                Assert.False(View(schedule,now).GetProperty("enabled").GetBoolean());
                Assert.False(schedule.ApplyBackend(config with {ConfigurationVersion=1},now));
            }
        }
        finally{Directory.Delete(root,true);}
    }
}
