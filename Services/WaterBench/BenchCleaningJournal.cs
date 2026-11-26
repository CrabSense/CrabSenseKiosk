using System.Globalization;
using System.Text.Json;

namespace CrabSenseKiosk.Services.WaterBench;

internal sealed record BenchCleanStatus(string Step,int DirtyMask,int SelectedMask,int VerifiedMask,int Vial,int Cycle,
    bool StorageOk,bool ReadyForAssay)
{
    internal static BenchCleanStatus Parse(JsonElement e)
    {
        var s=new BenchCleanStatus(e.GetProperty("step").GetString()??"",e.GetProperty("dirty_mask").GetInt32(),
            e.GetProperty("selected_mask").GetInt32(),e.GetProperty("verified_mask").GetInt32(),
            e.GetProperty("vial").GetInt32(),e.GetProperty("cycle").GetInt32(),
            e.GetProperty("storage_ok").GetBoolean(),e.GetProperty("ready_for_assay").GetBoolean());
        s.Validate();return s;
    }
    internal void Validate()
    {
        if(Step is not ("idle" or "route" or "initial_drain" or "initial_total" or "source" or "fill" or "settle" or "drain" or "total" or "verify" or "complete" or "fault")||
            DirtyMask is <0 or >7||SelectedMask is <0 or >7||VerifiedMask is <0 or >7||(VerifiedMask&~SelectedMask)!=0||
            Vial is <0 or >3||Cycle is <0 or >5||(ReadyForAssay&&(!StorageOk||DirtyMask!=0||Step is not ("idle" or "complete"))))
            throw new FormatException("Invalid cleaning status.");
        if(Step=="idle"&&(SelectedMask!=0||VerifiedMask!=0||Vial!=0||Cycle!=0)||
            Step=="complete"&&(SelectedMask==0||VerifiedMask!=SelectedMask||Vial!=0||(StorageOk&&(DirtyMask&SelectedMask)!=0))||
            Step is not ("idle" or "complete" or "fault")&&
                (SelectedMask==0||Vial==0||(SelectedMask&Bit(Vial))==0||(VerifiedMask&Bit(Vial))!=0||
                 (Step is "route" or "initial_drain" or "initial_total"?Cycle!=0:Cycle<1)))
            throw new FormatException("Inconsistent cleaning status.");
    }
    internal static int Bit(int vial)=>vial is >=1 and <=3?1<<(vial-1):0;
    internal static int First(int mask)=>Enumerable.Range(1,3).FirstOrDefault(i=>(mask&Bit(i))!=0);
}
internal sealed record BenchCleanEvent(int Version,Guid RunId,Guid StationId,Guid ConnectionId,string BootId,
    int Sequence,string Type,DateTimeOffset At,int Mask,Guid? RequestId=null,string? Action=null,int? Vial=null,
    BenchCleanStatus? Status=null,bool? Accepted=null,string? Reason=null);

// Pure reducer used both before persistence and when reading immutable records.
internal sealed class BenchCleanTimeline
{
    internal const int MaxEvents=256;
    internal BenchCleanEvent? Start {get;private set;}
    internal BenchCleanStatus? LastStatus {get;private set;}
    internal int Sequence {get;private set;}
    internal string State {get;private set;}="unobserved";
    internal Guid? PendingId {get;private set;}
    internal string? PendingAction {get;private set;}
    internal int? PendingVial {get;private set;}
    internal bool Active=>State is "start_attempt" or "running_observed";
    private readonly HashSet<Guid> _requests=new();
    private static void Need(bool ok){if(!ok)throw new FormatException("Cleaning evidence scope or transition mismatch.");}
    internal void Apply(BenchCleanEvent e)
    {
        Need(e.Version==1&&e.RunId!=Guid.Empty&&e.ConnectionId!=Guid.Empty&&WireProtocol.BatchId(e.BootId)&&
            e.Sequence==Sequence+1&&e.Sequence<=MaxEvents&&e.At!=default&&e.Mask is >=1 and <=7);
        if(Start is null)
        {
            Need(e.Type=="start"&&e.RequestId==e.RunId&&e.Action=="cleanStart"&&e.Status is null&&e.Accepted is null&&e.Vial is null);
            Start=e;PendingId=e.RequestId;PendingAction=e.Action;_requests.Add(e.RunId);State="start_attempt";Sequence=e.Sequence;return;
        }
        Need(e.RunId==Start.RunId&&e.StationId==Start.StationId&&e.ConnectionId==Start.ConnectionId&&
            e.BootId==Start.BootId&&e.Mask==Start.Mask);
        switch(e.Type)
        {
            case "command":
                Need(Active&&PendingId is null&&e.RequestId.HasValue&&e.RequestId!=Guid.Empty&&e.Vial is >=1 and <=3&&
                    e.Status is null&&e.Accepted is null&&LastStatus!=null&&LastStatus.Vial==e.Vial&&
                    (e.Action=="cleanRoute"&&LastStatus.Step=="route"||e.Action=="cleanVerify"&&LastStatus.Step=="verify")&&
                    _requests.Add(e.RequestId.Value));
                PendingId=e.RequestId;PendingAction=e.Action;PendingVial=e.Vial;break;
            case "ack":
                Need(PendingId.HasValue&&e.RequestId==PendingId&&e.Action==PendingAction&&e.Vial==PendingVial&&
                    e.Accepted.HasValue&&e.Status is null&&
                    (e.Accepted.Value?e.Reason=="accepted":e.Reason is "rejected" or "unsupported"));
                if(e.Accepted==false)
                {
                    // A status transition may have preceded its ACK. Never reinterpret
                    // a contradicted observation as a confirmed cleaning outcome.
                    if(PendingAction=="cleanStart")State=LastStatus is null?"start_rejected":"interrupted_observed";
                    else if(LastStatus?.Vial!=PendingVial||
                        LastStatus?.Step!=(PendingAction=="cleanRoute"?"route":"verify"))State="interrupted_observed";
                }
                PendingId=null;PendingAction=null;PendingVial=null;break;
            case "status":
                Need(Active&&e.Status!=null&&e.RequestId is null&&e.Action is null&&e.Vial is null&&e.Accepted is null);
                var next=e.Status!;next.Validate();Need(next.SelectedMask==e.Mask&&next.Step!="idle");
                if(next.Step=="fault")
                {
                    State="fault_reported";LastStatus=next;break;
                }
                Need(next.StorageOk);
                if(LastStatus is null)
                    Need(next.Step=="route"&&next.Vial==BenchCleanStatus.First(e.Mask)&&next.VerifiedMask==0&&(next.DirtyMask&e.Mask)==e.Mask);
                else
                    Transition(LastStatus,next,e.Mask,PendingAction,PendingVial);
                LastStatus=next;State=next.Step=="complete"?"firmware_complete_reported":"running_observed";break;
            case "interrupted":
                Need((Active||PendingId.HasValue)&&e.Status is null&&e.Accepted is null&&e.RequestId is null&&
                    !string.IsNullOrWhiteSpace(e.Reason)&&e.Reason.Length<=100);
                State=LastStatus?.Step=="fault"?"fault_reported":"interrupted_observed";
                PendingId=null;PendingAction=null;PendingVial=null;break;
            default:throw new FormatException("Unsupported cleaning event.");
        }
        Sequence=e.Sequence;
    }
    private static void Transition(BenchCleanStatus prior,BenchCleanStatus next,int mask,string? action,int? vial)
    {
        if(prior.Step=="verify")
        {
            Need(action=="cleanVerify"&&vial==prior.Vial);
            int verified=prior.VerifiedMask|BenchCleanStatus.Bit(prior.Vial);
            Need(next.VerifiedMask==verified);
            if(verified==mask)
                Need(next.Step=="complete"&&next.DirtyMask==(prior.DirtyMask&~mask)&&next.Cycle==prior.Cycle);
            else
                Need(next.Step=="route"&&next.Vial==BenchCleanStatus.First(mask&~verified)&&next.DirtyMask==prior.DirtyMask);
            return;
        }
        Need(next.VerifiedMask==prior.VerifiedMask&&next.Vial==prior.Vial&&next.DirtyMask==prior.DirtyMask);
        string expected=prior.Step switch{
            "route"=>"initial_drain","initial_drain"=>"initial_total","initial_total"=>"source",
            "source"=>"fill","fill"=>"settle","settle"=>"drain","drain"=>"total",
            "total"=>next.Step=="source"?"source":"verify",_=>""};
        Need(next.Step==expected);
        if(prior.Step=="route")Need(action=="cleanRoute"&&vial==prior.Vial);
        int cycle=prior.Step=="initial_total"?1:prior.Step=="total"&&next.Step=="source"?prior.Cycle+1:prior.Cycle;
        Need(next.Cycle==cycle);
    }
}
internal sealed class BenchCleaningJournal(Guid station,Guid connection,string boot,Guid run,int mask,EvidenceSpool spool)
{
    private readonly List<BenchCleanEvent> _events=[];
    private BenchCleanTimeline _timeline=new();
    internal Guid RunId=>run;
    internal bool Active=>_timeline.Active;
    internal bool Unsettled=>Active||_timeline.PendingId.HasValue;
    internal string State=>_timeline.State;
    internal Guid? PendingId=>_timeline.PendingId;
    private BenchCleanEvent New(string type,DateTimeOffset at)=>new(1,run,station,connection,boot,_events.Count+1,type,at,mask);
    private void Write(BenchCleanEvent e)
    {
        // Validate before append; state advances only after the immutable file is committed.
        var replay=new BenchCleanTimeline();
        foreach(var row in _events)replay.Apply(row);
        replay.Apply(e);spool.CleanEvent(e);_events.Add(e);_timeline=replay;
    }
    internal void Begin(DateTimeOffset at)=>Write(New("start",at) with{RequestId=run,Action="cleanStart"});
    internal void Command(BenchCommand r,DateTimeOffset at)=>Write(New("command",at) with{RequestId=r.RequestId,Action=r.Action,Vial=r.Id});
    internal bool Ack(GatewayAck a,DateTimeOffset at)
    {
        if(PendingId!=a.RequestId)return false;
        Write(New("ack",at) with{RequestId=a.RequestId,Action=_timeline.PendingAction,Vial=_timeline.PendingVial,Accepted=a.Accepted,Reason=a.Reason});
        return true;
    }
    internal void Observe(BenchCleanStatus s,DateTimeOffset at)
    {
        if(!Active||s==_timeline.LastStatus)return;
        if(s.Step=="idle"){Interrupt("unexpected_idle",at);throw new FormatException("Cleaning reset before terminal evidence.");}
        Write(New("status",at) with{Status=s});
    }
    internal void Interrupt(string reason,DateTimeOffset at)
    {if(Unsettled)Write(New("interrupted",at) with{Reason=reason});}
}
internal static class BenchCleaningInventory
{
    internal static string Name(Guid id,int sequence)=>"clean-audit-"+id.ToString("N")+"-"+sequence.ToString("D4",CultureInfo.InvariantCulture)+".json";
    private static bool ParseName(string name,out Guid id,out int sequence)
    {
        id=Guid.Empty;sequence=0;
        return name.Length==54&&name.StartsWith("clean-audit-",StringComparison.Ordinal)&&
            Guid.TryParseExact(name.AsSpan(12,32),"N",out id)&&id!=Guid.Empty&&
            name[44]=='-'&&int.TryParse(name.AsSpan(45,4),NumberStyles.None,CultureInfo.InvariantCulture,out sequence)&&
            sequence is >=1 and <=BenchCleanTimeline.MaxEvents&&name==Name(id,sequence);
    }
    private sealed record RunView(Guid RunId,Guid? StationId,Guid? ConnectionId,string? BootId,string State,int Mask,
        DateTimeOffset? StartedAt,DateTimeOffset? LastObservedAt,int EventCount,BenchCleanStatus? LastStatus,
        Guid? PendingRequestId,bool PhysicalCleaningVerified=false,bool ChemicalResidueMeasured=false,bool CommandsReplayed=false);
    private static (RunView View,BenchCleanEvent[] Events) Read(string root,Guid id,IEnumerable<(string Path,int Seq)> files,Guid? live)
    {
        var timeline=new BenchCleanTimeline();var events=new List<BenchCleanEvent>();
        try
        {
            foreach(var file in files.OrderBy(x=>x.Seq))
            {
                var row=BenchCommandAudit.Object(BenchCommandAudit.Read(file.Path,4096)).Deserialize<BenchCleanEvent>(BenchMediaManifest.Json)
                    ??throw new FormatException("Invalid cleaning event.");
                if(row.RunId!=id||row.Sequence!=file.Seq)throw new FormatException("Cleaning file identity mismatch.");
                timeline.Apply(row);events.Add(row);
            }
            var start=timeline.Start??throw new FormatException("Empty cleaning journal.");
            // No process can resume a previous run, even if the last event is nonterminal.
            string state=(timeline.Active||timeline.PendingId.HasValue)&&live!=id?"interrupted_or_ack_unknown_no_replay":
                timeline.PendingId.HasValue&&!timeline.Active?"terminal_observation_ack_pending":timeline.State;
            return(new(id,start.StationId,start.ConnectionId,start.BootId,state,start.Mask,start.At,
                events[^1].At,events.Count,timeline.LastStatus,timeline.PendingId),events.ToArray());
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException or ArgumentException)
        {return(new(id,null,null,null,"invalid_journal",0,null,null,events.Count,null,null),[]);}
    }
    internal static object Query(string root,Guid? live,int skip,int take,Guid? run=null)
    {
        var files=Directory.EnumerateFiles(root).Take(100001).ToArray();
        int invalid=files.Length>100000?1:0;
        var groups=new Dictionary<Guid,List<(string Path,int Seq)>>();
        foreach(var file in files.Take(100000))
        {
            string name=Path.GetFileName(file);
            if(!name.StartsWith("clean-audit-",StringComparison.Ordinal))continue;
            if(!ParseName(name,out var id,out var seq)){invalid++;continue;}
            if(!groups.TryGetValue(id,out var rows))groups[id]=rows=[];
            rows.Add((file,seq));
        }
        if(run.HasValue)
        {
            if(!groups.TryGetValue(run.Value,out var rows))return new{found=false,inventoryComplete=invalid==0};
            var detail=Read(root,run.Value,rows,live);
            return new{found=true,inventoryComplete=invalid==0&&detail.View.State!="invalid_journal",summary=detail.View,events=detail.Events};
        }
        var views=groups.Select(g=>Read(root,g.Key,g.Value,live).View).OrderByDescending(v=>v.StartedAt).ThenBy(v=>v.RunId).ToArray();
        invalid+=views.Count(v=>v.State=="invalid_journal");
        return new{total=views.Length,skip,take,inventoryComplete=invalid==0,invalidFiles=invalid,items=views.Skip(skip).Take(take).ToArray(),
            physicalCleaningVerified=false,chemicalResidueMeasured=false,commandsReplayed=false};
    }
}
