using System.Text.Json;
using CrabSenseKiosk.Services.WaterBench;
using SkiaSharp;
using Xunit;

namespace CrabSenseKiosk.Tests;
public sealed class BenchMediaManifestTests
{
    [Fact]
    public void ImmutableManifestBindsActualJpegAndStableUploadIdentity()
    {
        string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            using var bitmap=new SKBitmap(32,32);bitmap.Erase(SKColors.Beige);
            using var image=SKImage.FromBitmap(bitmap);using var encoded=image.Encode(SKEncodedImageFormat.Jpeg,90);
            var bytes=encoded.ToArray();var now=DateTimeOffset.UtcNow;
            var frame=new ReceivedFrame(1,2,32,32,WireProtocol.Crc32(bytes),bytes,now);
            var identity=new BenchMediaIdentity(Guid.NewGuid(),Guid.NewGuid(),"0123456789abcdef","abcdef0123456789",
                "NO2","abcdef0123456789-1",now,"000000000001","000000000002");
            var spool=new EvidenceSpool(root,1048576,100);
            string id=spool.Frame(frame,new{test=true},identity);
            var manifest=JsonSerializer.Deserialize<BenchMediaManifest>(File.ReadAllText(Path.Combine(root,"media-manifest-"+id+".json")),BenchMediaManifest.Json)!;
            Assert.Equal(Guid.ParseExact(id,"N"),manifest.EventId);
            Assert.Equal(identity.ConnectionId,manifest.ConnectionId);
            Assert.Equal("pending",manifest.UploadState);
            Assert.Equal(3,Directory.GetFiles(root).Length);
            var body=JsonSerializer.SerializeToElement(manifest.UploadBody(File.ReadAllBytes(Path.Combine(root,manifest.LocalFile))),BenchMediaManifest.Json);
            Assert.Equal(manifest.EventId,body.GetProperty("eventId").GetGuid());
            Assert.Equal(Convert.ToBase64String(bytes),body.GetProperty("jpegBase64").GetString());
            Assert.Throws<IOException>(()=>manifest.UploadBody(new byte[bytes.Length]));
            Assert.Throws<IOException>(()=>(manifest with{LocalFile="../bad.jpg"}).UploadBody(bytes));
            Assert.Throws<FormatException>(()=>BenchMediaManifest.Create(Guid.NewGuid(),frame,identity with{SessionId=identity.BatchId+"-2"}));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
