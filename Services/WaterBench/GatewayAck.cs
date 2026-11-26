using System.Text.Json;
using System.Globalization;

namespace CrabSenseKiosk.Services.WaterBench;
internal sealed record GatewayHello(string Boot,uint LastSequence,uint UptimeMs);
internal sealed record GatewayAck(string Boot,Guid RequestId,uint Sequence,bool Accepted,string Reason,uint UptimeMs,string BatchId);
internal sealed record ClockObservation(string Boot,uint UptimeMs,DateTimeOffset EstimatedUtc,long RoundTripMs,long ObservedMonotonicMs)
{
    // Estimate only. No validated oscillator drift or physical sensor/exposure alignment.
    internal DateTimeOffset? Estimate(uint uptime,long nowMs)
    {
        if(nowMs<ObservedMonotonicMs||nowMs-ObservedMonotonicMs>1800000)return null;
        int delta=unchecked((int)(uptime-UptimeMs));
        if(Math.Abs((long)delta)>1800000)return null;
        return EstimatedUtc.AddMilliseconds(delta);
    }
}
internal static class GatewayWire
{
    internal static GatewayHello Hello(JsonElement e,Guid nonce)
    {
        var boot=e.GetProperty("boot_id").GetString();
        if(e.GetProperty("version").GetInt32()!=1||e.GetProperty("nonce").GetString()!=nonce.ToString("N")||
            !WireProtocol.BatchId(boot))throw new FormatException("Invalid gateway handshake.");
        return new(boot!,e.GetProperty("last_seq").GetUInt32(),e.GetProperty("uptime_ms").GetUInt32());
    }
    internal static GatewayAck Ack(JsonElement e)
    {
        var boot=e.GetProperty("boot_id").GetString();
        string? request=e.GetProperty("request_id").GetString(),reason=e.GetProperty("reason").GetString();
        string? batch=e.GetProperty("batch_id").GetString();
        bool accepted=e.GetProperty("accepted").GetBoolean();
        uint seq=e.GetProperty("seq").GetUInt32();
        if(e.GetProperty("version").GetInt32()!=1||!WireProtocol.BatchId(boot)||!Guid.TryParseExact(request,"N",out var id)||
            id==Guid.Empty||seq==0||batch is null||(batch.Length!=0&&!WireProtocol.BatchId(batch))||
            (accepted?reason!="accepted":reason is not ("rejected" or "boot_mismatch" or "sequence_mismatch" or "unsupported")))
            throw new FormatException("Invalid gateway acknowledgment.");
        return new(boot!,id,seq,accepted,reason!,e.GetProperty("uptime_ms").GetUInt32(),batch);
    }
    internal static string Command(string boot,uint seq,Guid request,string command)
    {
        if(!WireProtocol.BatchId(boot)||seq==0||request==Guid.Empty||string.IsNullOrEmpty(command)||command.Length>=80||
            command.Any(c=>c<32||c>126))throw new FormatException("Invalid gateway command.");
        return $"gw {boot} {seq.ToString(CultureInfo.InvariantCulture)} {request:N} {command}";
    }
}
