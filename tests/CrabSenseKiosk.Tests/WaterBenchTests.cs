using System.Text.Json;
using CrabSenseKiosk.Services.WaterBench;
using SkiaSharp;
using Xunit;

namespace CrabSenseKiosk.Tests;
public sealed class WaterBenchTests
{
    [Fact]
    public void RestartTerminatesHistoricalBatchExactlyOnceAndNeverReplays()
    {
        var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            var station=Guid.NewGuid();var connection=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
            var spool=new EvidenceSpool(root,4*1024*1024,200);
            var journal=new BatchObservationJournal(station,connection,"0123456789abcdef",spool);
            journal.Start("BATCH START abcdef0123456789 mode=manual mask=1",Guid.NewGuid(),now);
            var reopened=new EvidenceSpool(root,4*1024*1024,200);
            Assert.Equal(1,reopened.RecoverInterruptedBatches(station,now.AddSeconds(1)));
            Assert.Equal(0,reopened.RecoverInterruptedBatches(station,now.AddSeconds(2)));
            var rows=Directory.GetFiles(root,"batch-timeline-*.json").Select(f=>
                JsonSerializer.Deserialize<BenchBatchEvent>(File.ReadAllText(f),BenchMediaManifest.Json)!).OrderBy(e=>e.Sequence).ToArray();
            Assert.Equal(2,rows.Length);Assert.Equal(connection,rows[1].ConnectionId);
            Assert.Equal("interrupted",rows[1].Type);Assert.Equal("kiosk_restart_no_replay",rows[1].Reason);
            Assert.Empty(Directory.GetFiles(root,"command-*.json"));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]
    public void CommandsAreBoundedAndNotRaw()
    {
        var id=Guid.NewGuid();
        Assert.Equal("batch start manual 5",WaterBenchSession.BuildCommand(new(id,"manual",Mask:5)));
        Assert.Equal("batch start scheduled",WaterBenchSession.BuildCommand(new(id,"scheduled",Mask:7)));
        Assert.Null(WaterBenchSession.BuildCommand(new(id,"scheduled",Mask:3)));
        Assert.Null(WaterBenchSession.BuildCommand(new(id,"manual",Mask:8)));
        Assert.Null(WaterBenchSession.BuildCommand(new(id,"pulse")));
        Assert.Null(WaterBenchSession.BuildCommand(new(id,"batch image 1 5\nstop")));
        Assert.Equal("nh3 profile 100 200 300 400 500 600",WaterBenchSession.BuildCommand(new(id,"assayProfile",Id:3,Values:new uint[]{100,200,300,400,500,600})));
        Assert.Null(WaterBenchSession.BuildCommand(new(id,"cleanProfile",Id:4,Values:new uint[]{100,200,300,400,500,2})));
        Assert.Null(WaterBenchSession.BuildCommand(new(id,"cleanProfile",Id:1,Values:new uint[]{100,200,300,400,500,6})));
    }
    [Fact]
    public void CameraChecksCrcOffsetsAndDecode()
    {
        using var image=new SKBitmap(16,16);
        image.Erase(SKColors.Yellow);
        using var encoded=image.Encode(SKEncodedImageFormat.Jpeg,90);
        Assert.NotNull(encoded);
        var bytes=encoded!.ToArray();var crc=WireProtocol.Crc32(bytes);var receiver=new CameraReceiver();var now=DateTimeOffset.UtcNow;
        Assert.Equal(0xcbf43926u,WireProtocol.Crc32("123456789"u8));
        string header=$"CAM BEGIN 5 1 16 16 {bytes.Length} {crc:x8}";
        receiver.Feed(header,now,0);
        Assert.Throws<FormatException>(()=>receiver.Feed("CAM DATA 5 1 ab",now,1));
        receiver.Feed(header,now,2);
        for(int i=0;i<bytes.Length;i+=32) receiver.Feed($"CAM DATA 5 {i} {Convert.ToHexString(bytes.AsSpan(i,Math.Min(32,bytes.Length-i)))}",now,3);
        var result=receiver.Feed("CAM END 5",now,4);
        Assert.NotNull(result);Assert.Equal(bytes,result!.Bytes);
        receiver.Feed(header,now,5);
        Assert.Throws<FormatException>(()=>receiver.Feed("CAM END 5",now,6));
    }
    [Fact]
    public void RejectsInvalidJpegEvenWithValidEnvelopeAndCrc()
    {
        byte[] invalid={0xff,0xd8,0xff,0xd9};
        var receiver=new CameraReceiver();
        var now=DateTimeOffset.UtcNow;
        receiver.Feed($"CAM BEGIN 5 1 16 16 4 {WireProtocol.Crc32(invalid):x8}",now,0);
        receiver.Feed("CAM DATA 5 0 FFD8FFD9",now,1);
        Assert.Throws<FormatException>(()=>receiver.Feed("CAM END 5",now,2));
        Assert.Throws<FormatException>(()=>WireProtocol.ValidateJpeg(invalid,16,16));
        Assert.Throws<FormatException>(()=>WireProtocol.ValidateJpeg(invalid,0,16));
        Assert.Throws<FormatException>(()=>WireProtocol.ValidateJpeg(invalid,2000,2000));
    }
    [Fact]
    public void RejectsWrongDimensionsAndCrc()
    {
        using var image=new SKBitmap(16,16);
        image.Erase(SKColors.Yellow);
        using var encoded=image.Encode(SKEncodedImageFormat.Jpeg,90);
        Assert.NotNull(encoded);
        var bytes=encoded!.ToArray();
        WireProtocol.ValidateJpeg(bytes,16,16);
        Assert.Throws<FormatException>(()=>WireProtocol.ValidateJpeg(bytes,15,16));
        var receiver=new CameraReceiver();var now=DateTimeOffset.UtcNow;
        receiver.Feed($"CAM BEGIN 5 1 16 16 {bytes.Length} {(WireProtocol.Crc32(bytes)^1u):x8}",now,0);
        for(int i=0;i<bytes.Length;i+=32) receiver.Feed($"CAM DATA 5 {i} {Convert.ToHexString(bytes.AsSpan(i,Math.Min(32,bytes.Length-i)))}",now,1);
        Assert.Throws<FormatException>(()=>receiver.Feed("CAM END 5",now,2));
    }
    [Fact]
    public void SpoolDoesNotOverwriteCommandAndControlDefaultsOff()
    {
        string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            var spool=new EvidenceSpool(root,1048576,100);
            Guid id=Guid.NewGuid();spool.Command(id,new { a=1 });
            Assert.Throws<IOException>(()=>spool.Command(id,new { a=2 }));
            var session=new WaterBenchSession(new WaterBenchOptions(),spool);
            var writes=new List<string>();session.Connected(writes.Add);
            Assert.False(session.TryCommand(new(Guid.NewGuid(),"manual",Mask:7),out _));
            Assert.Empty(writes);
        }
        finally{Directory.Delete(root,true);}
    }
}
