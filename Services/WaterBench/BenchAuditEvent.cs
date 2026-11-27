using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CrabSenseKiosk.Services.WaterBench;

internal sealed record BenchAuditEvent(Guid StationId,Guid EventId,Guid ConnectionId,string BootId,
    string Kind,Guid JournalId,int Sequence,DateTimeOffset At,string Payload)
{
    internal static Guid Id(Guid station,string kind,Guid journal,int sequence)=>new(SHA256.HashData(Encoding.UTF8.GetBytes(
        FormattableString.Invariant($"water-bench-audit-v1:{station:N}:{kind}:{journal:N}:{sequence}"))).AsSpan(0,16));
    internal string StreamId=>Id(StationId,Kind=="clean_status"?"clean-stream":"command-stream",JournalId,0).ToString("N")[..16];
    internal int Order=>Kind=="command"?0:Kind=="command_outcome"?1:Sequence;
    internal static BenchAuditEvent Create(Guid station,Guid connection,string boot,string kind,Guid journal,int seq,DateTimeOffset at,byte[] bytes)
        =>new(station,Id(station,kind,journal,seq),connection,boot,kind,journal,seq,at,new UTF8Encoding(false,true).GetString(bytes));
    internal void Validate()
    {
        if(StationId==Guid.Empty||ConnectionId==Guid.Empty||JournalId==Guid.Empty||!WireProtocol.BatchId(BootId)||
            EventId!=Id(StationId,Kind,JournalId,Sequence)||At==default||Kind is not ("command" or "command_outcome" or "clean_status")||
            Payload is null||Encoding.UTF8.GetByteCount(Payload)>16384)throw new FormatException("Audit envelope.");
        var p=BenchCommandAudit.Object(Encoding.UTF8.GetBytes(Payload));
        if(p.GetProperty("version").GetInt32()!=1||p.GetProperty("connectionId").GetGuid()!=ConnectionId||
            p.GetProperty("bootId").GetString()!=BootId||p.GetProperty("at").GetDateTimeOffset()!=At)
            throw new FormatException("Audit scope.");
        if(Kind=="clean_status")
        {
            var e=p.Deserialize<BenchCleanEvent>(BenchMediaManifest.Json)!;
            if(e.StationId!=StationId||e.RunId!=JournalId||e.Sequence!=Sequence||Sequence is <1 or >256)
                throw new FormatException("Cleaning identity.");
        }
        else if(Sequence!=0||p.GetProperty("requestId").GetGuid()!=JournalId)
            throw new FormatException("Command identity.");
        else if(Kind=="command")
        {
            var a=p.Deserialize<BenchCommandAttempt>(BenchMediaManifest.Json)!;
            BenchCommandAudit.Validate(a,JournalId);
            if(a.StationId!=StationId)throw new FormatException("Command station.");
        }
    }
}
