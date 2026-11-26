using CrabSenseKiosk.Services.WaterBench;
using System.Text.Json;
using Xunit;

namespace CrabSenseKiosk.Tests;
public sealed class BenchScheduleTests
{
    private static DateTimeOffset Utc(int hour,int minute)=>new(2026,11,26,hour,minute,0,TimeSpan.Zero);
    [Fact]
    public void CalendarVietnamMidnightAndBounds()
    {
        var s=ScheduleCalendar.Validate(new(true,new[]{"23:59","00:10"},5));
        Assert.Equal(Utc(16,59),ScheduleCalendar.Latest(s,Utc(17,5)));
        Assert.Equal(Utc(17,10),ScheduleCalendar.Next(s,Utc(17,5)));
        Assert.Throws<ArgumentException>(()=>ScheduleCalendar.Validate(new(true,new[]{"23:59","00:01"},5)));
        Assert.Throws<ArgumentException>(()=>ScheduleCalendar.Validate(new(true,new[]{"7:00"})));
        Assert.Throws<ArgumentException>(()=>ScheduleCalendar.Validate(new(true,new[]{"07:00","07:00"})));
        Assert.Null(ScheduleCalendar.Next(new(false,Array.Empty<string>()),Utc(0,0)));
    }
    private static JsonElement Status(BenchSchedule s,DateTimeOffset now)=>JsonSerializer.SerializeToElement(s.Snapshot(now),new JsonSerializerOptions(JsonSerializerDefaults.Web));
    [Fact]
    public void DueRequiresApprovalAndRestartDoesNotReplay()
    {
        string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            var options=new WaterBenchOptions{Enabled=true,EnableSupervisedControl=true};
            var session=new WaterBenchSession(options,new EvidenceSpool(Path.Combine(root,"spool"),1048576,100));
            Guid id;
            using(var s=new BenchSchedule(options,session,root,Utc(0,0)))
            {
                Assert.True(s.Configure(new(true,new[]{"07:10"},5),Utc(0,0),out _));
                s.Tick(Utc(0,10));
                var slot=Status(s,Utc(0,10)).GetProperty("lastSlot");
                id=slot.GetProperty("id").GetGuid();
                Assert.Equal("due",slot.GetProperty("state").GetString());
                Assert.False(s.Approve(id,Utc(0,11),out _)); // no identified controllers
                s.Tick(Utc(0,11));
                Assert.Equal(id,Status(s,Utc(0,11)).GetProperty("lastSlot").GetProperty("id").GetGuid());
            }
            using(var s=new BenchSchedule(options,session,root,Utc(0,12)))
            {
                s.Tick(Utc(0,12));
                Assert.Equal("interrupted",Status(s,Utc(0,12)).GetProperty("lastSlot").GetProperty("state").GetString());
                Assert.False(s.Approve(id,Utc(0,12),out _));
                s.Tick(Utc(0,9)); // backward wall clock never recreates slot
                Assert.Equal(id,Status(s,Utc(0,9)).GetProperty("lastSlot").GetProperty("id").GetGuid());
            }
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact]
    public void MissedWindowAndCorruptJournalFailClosed()
    {
        string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            var options=new WaterBenchOptions{Enabled=true};
            var session=new WaterBenchSession(options,new EvidenceSpool(Path.Combine(root,"spool"),1048576,100));
            using(var s=new BenchSchedule(options,session,root,Utc(0,0)))
            {
                Assert.True(s.Configure(new(true,new[]{"07:10"},5),Utc(0,0),out _));
                s.Tick(Utc(0,20));
                Assert.Equal("missed",Status(s,Utc(0,20)).GetProperty("lastSlot").GetProperty("state").GetString());
            }
            File.WriteAllText(Path.Combine(root,"schedule.json"),"{");
            using(var s=new BenchSchedule(options,session,root,Utc(0,21)))
            {
                Assert.False(Status(s,Utc(0,21)).GetProperty("storageHealthy").GetBoolean());
                Assert.False(s.Configure(new(true,new[]{"08:00"}),Utc(0,21),out _));
            }
        }
        finally{Directory.Delete(root,true);}
    }
}
