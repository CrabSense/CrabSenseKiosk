using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrabSenseKiosk.Services.WaterBench;

// Separate bounded operational journal, never modifies original evidence or receipts.
// One worker owns the journal; an invalid journal fails closed until inspected.
internal sealed record BenchRetryEntry(string BootId,string BatchId,Guid EventId,string Kind,string LocalSha256,
    int Attempts,DateTimeOffset NextAt,string Reason);
internal sealed record BenchRetryState(int Version,Guid StationId,string BackendOrigin,DateTimeOffset? GlobalUntil,
    BenchRetryEntry[] Entries);
internal sealed class BenchSyncRetryJournal : IDisposable
{
    internal const int MaxEntries=2048;
    private const int MaxBytes=1024*1024;
    private readonly string _path;
    private readonly FileStream _lease;
    private BenchRetryState _state;
    private bool _fault;
    internal BenchSyncRetryJournal(string directory,Guid station,Uri endpoint)
    {
        if(station==Guid.Empty||endpoint.Scheme!="https")throw new IOException("Retry scope not configured.");
        Directory.CreateDirectory(directory);
        if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked retry directory.");
        _path=Path.Combine(directory,"retry.json");
        _lease=new FileStream(_path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var origin=endpoint.GetLeftPart(UriPartial.Authority);
        _state=new(1,station,origin,null,[]);
        try
        {
            if(File.Exists(_path))
            {
                if((File.GetAttributes(_path)&FileAttributes.ReparsePoint)!=0||new FileInfo(_path).Length is <2 or >MaxBytes)
                    throw new IOException("Invalid retry journal file.");
                var saved=JsonSerializer.Deserialize<BenchRetryState>(File.ReadAllBytes(_path),BenchMediaManifest.Json)
                    ??throw new IOException("Invalid retry journal.");
                if(saved.Version!=1||saved.StationId!=station||saved.BackendOrigin!=origin||
                    saved.Entries is null||saved.Entries.Length>MaxEntries||
                    saved.Entries.Any(x=>x is null||!WireProtocol.BatchId(x.BootId)||!WireProtocol.BatchId(x.BatchId)||
                        x.EventId==Guid.Empty||x.Kind is not ("batch" or "media" or "nh3" or "audit")||
                        x.LocalSha256 is null||!Regex.IsMatch(x.LocalSha256,@"\A[0-9a-f]{64}\z")||
                        x.Attempts is <1 or >20||x.NextAt==default||x.Reason is null||x.Reason.Length>80)||
                    saved.Entries.Select(x=>(x.BootId,x.BatchId)).Distinct().Count()!=saved.Entries.Length)
                    throw new IOException("Retry journal scope or content mismatch.");
                _state=saved;
            }
        }
        catch{_lease.Dispose();throw;}
    }
    private void Save(BenchRetryState state)
    {
        if(_fault)throw new IOException("Retry journal fault.");
        string temp=_path+".tmp";
        try
        {
            var bytes=JsonSerializer.SerializeToUtf8Bytes(state,BenchMediaManifest.Json);
            if(bytes.Length>MaxBytes||state.Entries.Length>MaxEntries)throw new IOException("Retry journal capacity.");
            // The exclusive lease protects this temporary path. A crash may leave an uncommitted
            // temp; replace it, never treat it as acknowledged evidence.
            if(File.Exists(temp))File.Delete(temp);
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {stream.Write(bytes);stream.Flush(flushToDisk:true);}
            File.Move(temp,_path,overwrite:true);_state=state;
        }
        catch{_fault=true;throw;}
        finally{try{if(File.Exists(temp))File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}}
    }
    private BenchRetryEntry? Entry(BenchSyncCandidate c)=>_state.Entries.SingleOrDefault(x=>x.BootId==c.BootId&&x.BatchId==c.BatchId);
    internal bool GlobalPaused(DateTimeOffset now)=>_state.GlobalUntil is {} until&&until>now;
    internal bool Eligible(BenchSyncCandidate c,DateTimeOffset now)=>!_fault&&c.StationId==_state.StationId&&
        !GlobalPaused(now)&&(Entry(c) is not {} e||e.NextAt<=now);
    internal void Reconcile(BenchSyncQueue queue)
    {
        // A crash after the receipt write but before clearing retry metadata must not
        // leave a completed batch consuming journal space. Only verified local ACKs prune.
        var retained=_state.Entries.Where(e=>!queue.HasVerifiedReceipt(e.EventId,e.Kind,e.LocalSha256)).ToArray();
        if(retained.Length!=_state.Entries.Length)Save(_state with{Entries=retained});
    }
    internal void Begin(BenchSyncCandidate c,DateTimeOffset now)
    {
        if(!Eligible(c,now))throw new IOException("Retry not eligible.");
        var prev=Entry(c);
        int attempts=prev is not null&&prev.EventId==c.EventId&&prev.LocalSha256==c.LocalSha256?
            Math.Min(20,prev.Attempts+1):1;
        // Persist an uncertain-attempt cooldown before any HTTP transmission.
        Replace(c,new(c.BootId,c.BatchId,c.EventId,c.Kind,c.LocalSha256,attempts,now.AddSeconds(90),"attempt_pending"),_state.GlobalUntil);
    }
    private void Replace(BenchSyncCandidate c,BenchRetryEntry? value,DateTimeOffset? global)
    {
        var rows=_state.Entries.Where(e=>e.BootId!=c.BootId||e.BatchId!=c.BatchId).ToList();
        if(value!=null)rows.Add(value);
        Save(_state with{Entries=rows.ToArray(),GlobalUntil=global});
    }
    internal void Finish(BenchSyncCandidate c,BenchBatchSyncOutcome outcome,DateTimeOffset now)
    {
        var row=Entry(c);
        if(row is null||row.EventId!=c.EventId||row.LocalSha256!=c.LocalSha256||row.Reason!="attempt_pending")
            throw new IOException("Retry attempt mismatch.");
        if(outcome.Acknowledged){Replace(c,null,_state.GlobalUntil);return;}
        int backoff=Math.Min(1800,30*(1<<Math.Min(6,row.Attempts-1)));
        // Stable small jitter avoids every kiosk retrying simultaneously.
        int jitter=c.EventId.ToByteArray()[0]%11;
        int seconds=Math.Min(1800,backoff+jitter);
        if(outcome.State is "credential_rejected")seconds=Math.Max(seconds,300);
        if(outcome.State is "backend_conflict" or "backend_rejected" or "invalid_receipt" or
            "local_evidence_invalid" or "local_evidence_or_receipt_error")seconds=Math.Max(seconds,120);
        seconds=Math.Max(seconds,Math.Clamp(outcome.RetryAfterSeconds??0,0,86400));
        var due=now.AddSeconds(seconds);
        bool global=outcome.State is "credential_rejected" or "backend_rate_limited" or "transport_unavailable" or "transport_timeout";
        var globalUntil=global&&(_state.GlobalUntil is null||_state.GlobalUntil<due)?due:_state.GlobalUntil;
        Replace(c,row with{NextAt=due,Reason=outcome.State},globalUntil);
    }
    internal (int Deferred,DateTimeOffset? NextRetryAt,DateTimeOffset? GlobalUntil) Status(DateTimeOffset now)
    {
        var deferred=_state.Entries.Where(x=>x.NextAt>now).ToArray();
        var global=GlobalPaused(now)?_state.GlobalUntil:null;
        DateTimeOffset? next=deferred.Length==0?null:deferred.Min(x=>x.NextAt);
        if(global.HasValue&&(next is null||next<global))next=global;
        return(deferred.Length,next,global);
    }
    internal IReadOnlyList<BenchRetryEntry> Entries()=>_state.Entries.ToArray();
    public void Dispose()=>_lease.Dispose();
}
