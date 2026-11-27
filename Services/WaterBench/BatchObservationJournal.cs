using System.Globalization;
using System.Text.Json;

namespace CrabSenseKiosk.Services.WaterBench;

// Contract matching BE P2.2.3. This is an observation, not an analytical result.
internal sealed record BenchBatchEvent(Guid StationId,Guid EventId,Guid ConnectionId,string BootId,string BatchId,int Sequence,
    string Type,DateTimeOffset ReceivedAt,string? Mode=null,int? Mask=null,string? Analyte=null,string? SessionId=null,
    uint? ReactionStartMs=null,int? ReactionDurationMs=null,int? CaptureId=null,int? State=null,
    int? Started=null,int? Reacting=null,int? Imaged=null,int? Drained=null,Guid? RequestId=null,string? Reason=null);

internal sealed class BatchObservationJournal(Guid station,Guid connection,string boot,EvidenceSpool spool)
{
    private string? _batch;
    private int _seq;
    private int _mask;
    private bool _terminal;
    private (int State,int Started,int Reacting,int Imaged,int Drained)? _status;
    private readonly Dictionary<string,string> _sessions=new(StringComparer.Ordinal);
    internal bool Active=>_batch is not null&&!_terminal;
    private BenchBatchEvent New(string type,DateTimeOffset at)=>new(station,Guid.NewGuid(),connection,boot,_batch!,_seq+1,type,at);
    private void Write(BenchBatchEvent e)
    {
        if(e.Sequence>512||e.Sequence==512&&e.Type!="interrupted"&&!(e.Type=="status"&&e.State is 2 or 3))throw new IOException("Batch timeline capacity reached.");
        spool.BatchEvent(e);_seq=e.Sequence;
    }
    internal void Start(string line,Guid? request,DateTimeOffset at)
    {
        var p=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        if(station==Guid.Empty||connection==Guid.Empty||!WireProtocol.BatchId(boot)||Active||
            p.Length!=5||p[0]!="BATCH"||p[1]!="START"||!WireProtocol.BatchId(p[2])||
            p[3] is not ("mode=manual" or "mode=scheduled")||!p[4].StartsWith("mask=",StringComparison.Ordinal)||
            !int.TryParse(p[4][5..],NumberStyles.None,CultureInfo.InvariantCulture,out var mask)||mask is <1 or >7||
            (p[3]=="mode=scheduled"&&mask!=7))throw new FormatException("Invalid batch start observation.");
        _batch=p[2];_seq=0;_mask=mask;_terminal=false;_status=null;_sessions.Clear();
        Write(New("start",at) with{Mode=p[3][5..],Mask=mask,RequestId=request});
    }
    internal void Session(JsonElement e,DateTimeOffset at)
    {
        RequireBatch(e);string a=e.GetProperty("analyte").GetString()!,sid=e.GetProperty("session_id").GetString()!;
        if(!Selected(a)||!(a=="NH3"?WireProtocol.BatchId(sid):sid==_batch+(a=="NO2"?"-1":"-2"))||_sessions.ContainsKey(a))
            throw new FormatException("Invalid batch session observation.");
        Write(New("session",at) with{Analyte=a,SessionId=sid});_sessions.Add(a,sid);
    }
    private bool Selected(string a)=>(_mask&(a switch{"NO2"=>1,"NO3"=>2,"NH3"=>4,_=>0}))!=0;
    private void RequireBatch(JsonElement e)
    {if(!Active||e.GetProperty("batch_id").GetString()!=_batch)throw new FormatException("Unexpected batch observation.");}
    internal void Reaction(string line,DateTimeOffset at)
    {
        var p=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        if(!Active||p.Length!=6||p[0]!="BATCH"||p[1]!="REACTION"||p[2]!=_batch||
            !p[4].StartsWith("start=",StringComparison.Ordinal)||!p[5].StartsWith("duration=",StringComparison.Ordinal)||
            !uint.TryParse(p[4][6..],NumberStyles.None,CultureInfo.InvariantCulture,out var start)||
            !int.TryParse(p[5][9..],NumberStyles.None,CultureInfo.InvariantCulture,out var duration))
            throw new FormatException("Invalid reaction observation.");
        string a=p[3] switch{"1"=>"NO2","2"=>"NO3","3"=>"NH3",_=>""};
        if(!_sessions.TryGetValue(a,out var sid)||duration!=(a=="NO2"?300000:a=="NO3"?120000:600000))
            throw new FormatException("Reaction session or duration mismatch.");
        Write(New("reaction",at) with{Analyte=a,SessionId=sid,ReactionStartMs=start,ReactionDurationMs=duration});
    }
    internal void Image(JsonElement e,DateTimeOffset at)
    {
        RequireBatch(e);string a=e.GetProperty("analyte").GetString()!,sid=e.GetProperty("session_id").GetString()!;
        int id=e.GetProperty("capture_id").GetInt32();
        if(!_sessions.TryGetValue(a,out var expected)||sid!=expected||id is <1 or >65536)throw new FormatException("Image session mismatch.");
        Write(New("image",at) with{Analyte=a,SessionId=sid,CaptureId=id});
    }
    internal void Status(JsonElement e,DateTimeOffset at)
    {
        int state=e.GetProperty("state").GetInt32();
        if(!Active||state==0)return;
        RequireBatch(e);
        var next=(State:state,Started:e.GetProperty("started").GetInt32(),Reacting:e.GetProperty("reacting").GetInt32(),
            Imaged:e.GetProperty("imaged").GetInt32(),Drained:e.GetProperty("drained").GetInt32());
        if(e.GetProperty("mask").GetInt32()!=_mask||state is <1 or >3)throw new FormatException("Batch mask/state mismatch.");
        if(_status==next)return;
        Write(New("status",at) with{Mask=_mask,State=state,Started=next.Started,Reacting=next.Reacting,Imaged=next.Imaged,Drained=next.Drained});
        _status=next;_terminal=state is 2 or 3;
    }
    internal void Interrupt(string reason,DateTimeOffset at)
    {
        if(!Active)return;
        Write(New("interrupted",at) with{Reason=reason});_terminal=true;
    }
}
