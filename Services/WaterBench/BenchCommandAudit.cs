using System.Security.Cryptography;
using System.Text.Json;

namespace CrabSenseKiosk.Services.WaterBench;

internal sealed record BenchCommandAttempt(int Version,Guid RequestId,Guid StationId,Guid ConnectionId,
    string BootId,uint Sequence,BenchCommand Request,string Command,DateTimeOffset At,string State="send_attempt");
internal sealed record BenchCommandOutcome(int Version,Guid RequestId,Guid ConnectionId,string BootId,uint Sequence,
    string AttemptSha256,string State,string Reason,DateTimeOffset At,GatewayAck? Ack=null,
    DateTimeOffset? EstimatedControllerUtc=null);
internal sealed record BenchCommandAuditView(Guid RequestId,string State,string? Action,Guid? StationId,
    Guid? ConnectionId,string? BootId,uint? Sequence,DateTimeOffset? AttemptAt,DateTimeOffset? OutcomeAt,
    string? AttemptSha256,string? Reason,JsonElement? Attempt,JsonElement? Outcome);

internal static class BenchCommandAudit
{
    internal static string Digest(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static byte[] Read(string path,int limit)
    {
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked command journal.");
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length<2||stream.Length>limit)throw new IOException("Command journal size.");
        var bytes=new byte[checked((int)stream.Length)];stream.ReadExactly(bytes);return bytes;
    }
    internal static JsonElement Object(byte[] bytes)
    {
        using var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=16});
        var e=doc.RootElement;
        if(e.ValueKind!=JsonValueKind.Object)throw new FormatException("Journal object required.");
        CheckKeys(e);return e.Clone();
    }
    private static void CheckKeys(JsonElement e)
    {
        if(e.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var p in e.EnumerateObject())
            {if(!names.Add(p.Name))throw new FormatException("Duplicate journal property.");CheckKeys(p.Value);}
        }
        else if(e.ValueKind==JsonValueKind.Array)foreach(var item in e.EnumerateArray())CheckKeys(item);
    }
    internal static void Validate(BenchCommandAttempt a,Guid id)
    {
        if(a.Version!=1||id==Guid.Empty||a.RequestId!=id||a.ConnectionId==Guid.Empty||
            !WireProtocol.BatchId(a.BootId)||a.Sequence==0||a.At==default||a.State!="send_attempt"||
            a.Request is null||a.Request.RequestId!=id||a.Request.Action is "stop" or "batchStatus" or "cleanStatus"||
            WaterBenchSession.BuildCommand(a.Request) is not string command||command!=a.Command)
            throw new FormatException("Invalid command attempt.");
    }
    internal static void Validate(BenchCommandOutcome o,BenchCommandAttempt a,string hash)
    {
        if(o.Version!=1||o.RequestId!=a.RequestId||o.ConnectionId!=a.ConnectionId||o.BootId!=a.BootId||
            o.Sequence!=a.Sequence||o.AttemptSha256!=hash||o.At==default||
            o.State is not ("accepted" or "rejected" or "unknown")||
            string.IsNullOrWhiteSpace(o.Reason)||o.Reason.Length>100||o.Reason.Any(char.IsControl))
            throw new FormatException("Outcome does not bind attempt.");
        if(o.State=="unknown")
        {
            if(o.Ack!=null||o.EstimatedControllerUtc.HasValue)throw new FormatException("Unknown outcome cannot contain an ACK.");
            return;
        }
        var ack=o.Ack;
        if(ack is null||ack.RequestId!=a.RequestId||ack.Boot!=a.BootId||ack.Sequence!=a.Sequence||
            ack.Accepted!=(o.State=="accepted")||ack.Reason!=o.Reason||
            (ack.Accepted?ack.Reason!="accepted":ack.Reason is not ("rejected" or "unsupported"))||
            ack.BatchId is null||(ack.BatchId.Length!=0&&!WireProtocol.BatchId(ack.BatchId))||
            (ack.Accepted&&(a.Request.Action is "manual" or "scheduled")&&!WireProtocol.BatchId(ack.BatchId)))
            throw new FormatException("Invalid recorded ACK.");
    }
    internal static BenchCommandAuditView Inspect(string root,Guid id,Guid? pending)
    {
        var attemptPath=Path.Combine(root,"command-"+id.ToString("N")+".json");
        var outcomePath=Path.Combine(root,"outcome-"+id.ToString("N")+".json");
        try
        {
            var bytes=Read(attemptPath,16384);var a=Object(bytes);var hash=Digest(bytes);
            JsonElement? o=File.Exists(outcomePath)?Object(Read(outcomePath,16384)):null;
            if(!a.TryGetProperty("version",out _))
                return new(id,"legacy_unverified",null,null,null,null,null,null,null,hash,null,a,o);
            var attempt=a.Deserialize<BenchCommandAttempt>(BenchMediaManifest.Json)??throw new FormatException("Missing attempt.");
            Validate(attempt,id);
            BenchCommandOutcome? outcome=null;
            if(o.HasValue)
            {
                if(!o.Value.TryGetProperty("version",out _))throw new FormatException("Typed outcome required.");
                outcome=o.Value.Deserialize<BenchCommandOutcome>(BenchMediaManifest.Json)??throw new FormatException("Missing outcome.");
                Validate(outcome,attempt,hash);
            }
            return new(id,outcome?.State??(pending==id?"awaiting_ack":"unknown_no_replay"),attempt.Request.Action,
                attempt.StationId,attempt.ConnectionId,attempt.BootId,attempt.Sequence,attempt.At,outcome?.At,hash,outcome?.Reason,a,o);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException or ArgumentException)
        {return new(id,"invalid_journal",null,null,null,null,null,null,null,null,"inspection_required",null,null);}
    }
}
