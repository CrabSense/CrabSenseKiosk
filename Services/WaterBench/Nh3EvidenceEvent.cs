using System.Text.Json;

namespace CrabSenseKiosk.Services.WaterBench;

internal sealed record Nh3SampleReading(uint SampleUptimeMs,uint PhObservedUptimeMs,decimal? Ph,
    uint TemperatureObservedUptimeMs,decimal? TemperatureC,int TemperatureRegisterMaxAgeMs);
internal sealed record Nh3ImageObservation(int CaptureId,uint RequestedUptimeMs,uint ReactionFinishedUptimeMs);
internal sealed record Nh3ClockEstimate(DateTimeOffset? EstimatedSampleUtc,DateTimeOffset? EstimatedPhObservationUtc,
    DateTimeOffset? EstimatedTemperatureObservationUtc,long AnchorRoundTripMs);
internal sealed record Nh3EvidenceEvent(Guid StationId,Guid EventId,Guid ConnectionId,string BootId,string BatchId,
    string SessionId,string Type,DateTimeOffset ReceivedAt,string MasterHardwareId,string SlaveHardwareId,
    Nh3SampleReading? Sample=null,Nh3ImageObservation? Image=null,Nh3ClockEstimate? ClockEstimate=null);
internal static class Nh3EvidencePolicy
{
    public static uint Elapsed(uint later,uint earlier)=>unchecked(later-earlier);
    public static void Validate(Nh3EvidenceEvent e)
    {
        if(e.StationId==Guid.Empty||e.EventId==Guid.Empty||e.ConnectionId==Guid.Empty||
            !WireProtocol.BatchId(e.BootId)||!WireProtocol.BatchId(e.BatchId)||!WireProtocol.BatchId(e.SessionId)||
            !WireProtocol.HardwareId(e.MasterHardwareId)||!WireProtocol.HardwareId(e.SlaveHardwareId)||
            e.MasterHardwareId==e.SlaveHardwareId||e.ReceivedAt==default||e.ReceivedAt>DateTimeOffset.UtcNow.AddMinutes(5))
            throw new FormatException("Invalid NH3 evidence identity.");
        if(e.Type=="sample")
        {
            var s=e.Sample;
            if(s is null||e.Image!=null||s.TemperatureRegisterMaxAgeMs!=3000||
                s.Ph is <0 or >14||s.TemperatureC is <-10 or >60||
                (s.Ph.HasValue&&Elapsed(s.PhObservedUptimeMs,s.SampleUptimeMs)>1000)||
                (s.TemperatureC.HasValue&&Elapsed(s.TemperatureObservedUptimeMs,s.SampleUptimeMs)>1000))
                throw new FormatException("Invalid NH3 sample observation.");
            if(e.ClockEstimate is {} c)
            {
                bool ValidTime(DateTimeOffset? t)=>!t.HasValue||
                    (t.Value>=e.ReceivedAt.AddMinutes(-31)&&t.Value<=e.ReceivedAt.AddSeconds(5));
                if(c.AnchorRoundTripMs is <0 or >3000||!ValidTime(c.EstimatedSampleUtc)||
                    !ValidTime(c.EstimatedPhObservationUtc)||!ValidTime(c.EstimatedTemperatureObservationUtc)||
                    (!s.Ph.HasValue&&c.EstimatedPhObservationUtc.HasValue)||
                    (!s.TemperatureC.HasValue&&c.EstimatedTemperatureObservationUtc.HasValue))
                    throw new FormatException("Invalid estimated clock evidence.");
                bool Matches(DateTimeOffset? t,uint observed)=>!t.HasValue||!c.EstimatedSampleUtc.HasValue||
                    Math.Abs((t.Value-c.EstimatedSampleUtc.Value).TotalMilliseconds-(long)Elapsed(observed,s.SampleUptimeMs))<=1;
                if(!Matches(c.EstimatedPhObservationUtc,s.PhObservedUptimeMs)||
                    !Matches(c.EstimatedTemperatureObservationUtc,s.TemperatureObservedUptimeMs))
                    throw new FormatException("NH3 estimated clock mismatch.");
            }
        }
        else if(e.Type=="image_link")
        {
            if(e.Sample!=null||e.ClockEstimate!=null||e.Image is not {} i||i.CaptureId is <1 or >65536||
                Elapsed(i.RequestedUptimeMs,i.ReactionFinishedUptimeMs)>1800000)
                throw new FormatException("Invalid NH3 image observation.");
        }
        else throw new FormatException("Unsupported NH3 evidence kind.");
    }
}

internal static class Nh3EvidenceWire
{
    internal static Nh3SampleReading Sample(JsonElement e)
    {
        if(e.GetProperty("clock").GetString()!="boot_uptime"||
            e.GetProperty("sensor_vial_alignment_verified").GetBoolean()||
            e.GetProperty("salinity_ppt").ValueKind!=JsonValueKind.Null)
            throw new FormatException("Unexpected NH3 sample contract.");
        decimal? Number(string key)
        {
            var v=e.GetProperty(key);
            return v.ValueKind==JsonValueKind.Null?null:v.GetDecimal();
        }
        return new(e.GetProperty("sample_uptime_ms").GetUInt32(),e.GetProperty("ph_observed_uptime_ms").GetUInt32(),Number("ph"),
            e.GetProperty("temperature_observed_uptime_ms").GetUInt32(),Number("temperature_c"),
            e.GetProperty("temperature_register_max_age_ms").GetInt32());
    }
    internal static Nh3ImageObservation Image(JsonElement e)=>new(e.GetProperty("capture_id").GetInt32(),
        e.GetProperty("requested_uptime_ms").GetUInt32(),e.GetProperty("reaction_finished_uptime_ms").GetUInt32());
    internal static Nh3ClockEstimate? Estimate(Nh3SampleReading s,ClockObservation? clock,string boot,long now)
    {
        if(clock is not {} c||c.Boot!=boot)return null;
        var sample=c.Estimate(s.SampleUptimeMs,now);
        var ph=s.Ph.HasValue?c.Estimate(s.PhObservedUptimeMs,now):null;
        var temp=s.TemperatureC.HasValue?c.Estimate(s.TemperatureObservedUptimeMs,now):null;
        return sample is null&&ph is null&&temp is null?null:new(sample,ph,temp,c.RoundTripMs);
    }
}
