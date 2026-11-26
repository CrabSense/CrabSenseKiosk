using System.Text.Json;
using CrabSenseKiosk.Services.WaterBench;
using Xunit;

namespace CrabSenseKiosk.Tests;
public sealed class BatchObservationJournalTests
{
    [Fact]
    public void RecordsActualFirmwareLinesAndCoalescesRepeatedStatus()
    {
        string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            var station=Guid.NewGuid();var connection=Guid.NewGuid();var request=Guid.NewGuid();
            var journal=new BatchObservationJournal(station,connection,"0123456789abcdef",new EvidenceSpool(root,1048576,100));
            var now=DateTimeOffset.UtcNow;const string b="abcdef0123456789";
            journal.Start("BATCH START "+b+" mode=manual mask=1",request,now);
            journal.Session(JsonSerializer.SerializeToElement(new{batch_id=b,analyte="NO2",session_id=b+"-1"}),now);
            journal.Reaction("BATCH REACTION "+b+" 1 start=4294967295 duration=300000",now);
            journal.Image(JsonSerializer.SerializeToElement(new{batch_id=b,analyte="NO2",session_id=b+"-1",capture_id=65536}),now);
            var status=JsonSerializer.SerializeToElement(new{batch_id=b,state=1,mask=1,started=1,reacting=1,imaged=0,drained=0});
            journal.Status(status,now);journal.Status(status,now);
            journal.Interrupt("disconnect",now);journal.Interrupt("disconnect",now);
            var rows=Directory.GetFiles(root,"batch-timeline-*.json").Select(path=>
                JsonSerializer.Deserialize<BenchBatchEvent>(File.ReadAllText(path),new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .OrderBy(e=>e.Sequence).ToArray();
            Assert.Equal(6,rows.Length);
            Assert.Equal(Enumerable.Range(1,6),rows.Select(e=>e.Sequence));
            Assert.All(rows,e=>{Assert.Equal(station,e.StationId);Assert.Equal(connection,e.ConnectionId);Assert.Equal(b,e.BatchId);});
            Assert.Equal(request,rows[0].RequestId);Assert.Equal(uint.MaxValue,rows[2].ReactionStartMs);
            Assert.Equal(65536,rows[3].CaptureId);Assert.Equal("interrupted",rows[5].Type);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]
    public void ScheduledMaskAndReactionIdentityAreChecked()
    {
        string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            var journal=new BatchObservationJournal(Guid.NewGuid(),Guid.NewGuid(),"0123456789abcdef",new EvidenceSpool(root,1048576,100));
            Assert.Throws<FormatException>(()=>journal.Start("BATCH START abcdef0123456789 mode=scheduled mask=1",null,DateTimeOffset.UtcNow));
            journal.Start("BATCH START abcdef0123456789 mode=scheduled mask=7",null,DateTimeOffset.UtcNow);
            Assert.Throws<FormatException>(()=>journal.Reaction("BATCH REACTION abcdef0123456789 1 start=1 duration=300000",DateTimeOffset.UtcNow));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
