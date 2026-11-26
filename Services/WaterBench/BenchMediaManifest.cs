using System.Security.Cryptography;
using System.Text.Json;

namespace CrabSenseKiosk.Services.WaterBench;

// Stable metadata retained next to immutable JPEG; never infer identity from current config on retry.
internal sealed record BenchMediaIdentity(Guid StationId,Guid ConnectionId,string BootId,string BatchId,
    string Analyte,string SessionId,DateTimeOffset RequestedAt,string MasterHardwareId,string SlaveHardwareId);
internal sealed record BenchMediaManifest(Guid StationId,Guid EventId,Guid ConnectionId,string BootId,string BatchId,
    string Analyte,string SessionId,int CaptureId,uint Sequence,int Width,int Height,uint Crc32,string Sha256,
    DateTimeOffset ReceivedAt,DateTimeOffset RequestedAt,string MasterHardwareId,string SlaveHardwareId,
    int SizeBytes,string LocalFile,string UploadState="pending",bool ActualExposureVerified=false,bool ChemicalAccuracyValidated=false)
{
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    internal static BenchMediaManifest Create(Guid id,ReceivedFrame frame,BenchMediaIdentity identity)
    {
        if(id==Guid.Empty||identity.StationId==Guid.Empty||identity.ConnectionId==Guid.Empty||
            !WireProtocol.BatchId(identity.BootId)||!WireProtocol.BatchId(identity.BatchId)||
            identity.Analyte is not ("NO2" or "NO3" or "NH3")||
            !(identity.Analyte=="NH3"?WireProtocol.BatchId(identity.SessionId):
                identity.SessionId==identity.BatchId+(identity.Analyte=="NO2"?"-1":"-2"))||
            !WireProtocol.HardwareId(identity.MasterHardwareId)||!WireProtocol.HardwareId(identity.SlaveHardwareId)||
            identity.MasterHardwareId==identity.SlaveHardwareId||frame.CaptureId is 0 or >65536||
            Math.Abs((frame.ReceivedAt-identity.RequestedAt).TotalSeconds)>60)
            throw new FormatException("Invalid capture identity.");
        WireProtocol.ValidateJpeg(frame.Bytes,frame.Width,frame.Height);
        if(WireProtocol.Crc32(frame.Bytes)!=frame.Crc)throw new FormatException("CRC mismatch.");
        return new(identity.StationId,id,identity.ConnectionId,identity.BootId,identity.BatchId,identity.Analyte,identity.SessionId,
            checked((int)frame.CaptureId),frame.Sequence,frame.Width,frame.Height,frame.Crc,
            Convert.ToHexString(SHA256.HashData(frame.Bytes)).ToLowerInvariant(),frame.ReceivedAt,identity.RequestedAt,
            identity.MasterHardwareId,identity.SlaveHardwareId,frame.Bytes.Length,id.ToString("N")+".jpg");
    }
    // Only the future ordered uploader calls this after selecting a committed manifest.
    internal object UploadBody(byte[] bytes)
    {
        if(EventId==Guid.Empty||LocalFile!=EventId.ToString("N")+".jpg"||SizeBytes!=bytes.Length||
            bytes.Length>WireProtocol.MaxFrame||WireProtocol.Crc32(bytes)!=Crc32||
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()!=Sha256||
            UploadState!="pending"||ActualExposureVerified||ChemicalAccuracyValidated)
            throw new IOException("Stored image or manifest mismatch.");
        WireProtocol.ValidateJpeg(bytes,Width,Height);
        return new{StationId,EventId,ConnectionId,BootId,BatchId,Analyte,SessionId,CaptureId,Sequence,Width,Height,Crc32,Sha256,
            ReceivedAt,RequestedAt,MasterHardwareId,SlaveHardwareId,JpegBase64=Convert.ToBase64String(bytes)};
    }
}
