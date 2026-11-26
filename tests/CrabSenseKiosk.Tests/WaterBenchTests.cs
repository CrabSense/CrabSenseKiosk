using System.Text.Json;
using CrabSenseKiosk.Services.WaterBench;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Jpeg;
using Xunit;

namespace CrabSenseKiosk.Tests;
public sealed class WaterBenchTests
{
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
        using var image=new Image<Rgb24>(16,16);
        using var stream=new MemoryStream();image.Save(stream,new JpegEncoder());
        var bytes=stream.ToArray();var crc=WireProtocol.Crc32(bytes);var receiver=new CameraReceiver();var now=DateTimeOffset.UtcNow;
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
