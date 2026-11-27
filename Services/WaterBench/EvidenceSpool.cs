using System.Security.Cryptography;
using System.Text.Json;

namespace CrabSenseKiosk.Services.WaterBench;

// Immutable evidence with shared quota accounting for the serial writer and sync receipts.
internal sealed class EvidenceSpool
{
    private readonly string _root;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly object _gate=new();
    private readonly HashSet<Guid> _reservedReceipts=new();
    private readonly HashSet<Guid> _reservedOutcomes=new();
    internal const int OutcomeBudget=4096;
    internal const int ReceiptBudget=2048;
    internal string Root=>_root;
    internal int MaxFiles=>_maxFiles;
    private long _bytes;
    private int _files;
    internal EvidenceSpool(string root,long maxBytes,int maxFiles)
    {
        _root = Path.GetFullPath(root);
        _maxBytes = Math.Clamp(maxBytes,1_048_576L,4L*1024*1024*1024);
        _maxFiles = Math.Clamp(maxFiles,100,100000);
        Directory.CreateDirectory(_root);
        foreach (var path in Directory.EnumerateFiles(_root))
        {
            _files++;
            _bytes += new FileInfo(path).Length;
            var name=Path.GetFileName(path);
            if((BenchSyncQueue.TryFileId(name,"batch-timeline-",out var id)||BenchSyncQueue.TryFileId(name,"media-manifest-",out id)||BenchSyncQueue.TryFileId(name,"nh3-evidence-",out id)||BenchSyncQueue.TryFileId(name,"audit-event-",out id))&&
                !File.Exists(Path.Combine(_root,BenchSyncQueue.ReceiptName(id))))_reservedReceipts.Add(id);
            if(BenchSyncQueue.TryFileId(name,"command-",out var commandId)&&
                !File.Exists(Path.Combine(_root,"outcome-"+commandId.ToString("N")+".json")))_reservedOutcomes.Add(commandId);
        }
    }
    private bool Fits(int files,long bytes)=>_files+(long)_reservedReceipts.Count+_reservedOutcomes.Count+files<=_maxFiles&&
        _bytes+(long)_reservedReceipts.Count*ReceiptBudget+(long)_reservedOutcomes.Count*OutcomeBudget+bytes<=_maxBytes;
    internal bool HasCapacity {get{lock(_gate)return Fits(4,128*1024+ReceiptBudget);}}
    internal object CapacitySnapshot()
    {
        lock(_gate)return new{usedBytes=_bytes,usedFiles=_files,maxBytes=_maxBytes,maxFiles=_maxFiles,
            reservedReceiptFiles=_reservedReceipts.Count,reservedReceiptBytes=(long)_reservedReceipts.Count*ReceiptBudget,
            reservedCommandOutcomeFiles=_reservedOutcomes.Count,reservedCommandOutcomeBytes=(long)_reservedOutcomes.Count*OutcomeBudget,
            hasCapacity=Fits(4,128*1024+ReceiptBudget),basis="tracked_spool_files_excludes_subdirectories"};
    }
    private void PutQueued(string filename,Guid id,byte[] bytes)
    {
        lock(_gate)
        {
            if(id==Guid.Empty||File.Exists(Path.Combine(_root,BenchSyncQueue.ReceiptName(id)))||!_reservedReceipts.Add(id))
                throw new IOException("Duplicate queued event.");
            try{Put(filename,bytes);}
            catch{_reservedReceipts.Remove(id);throw;}
        }
    }
    internal void PutReceipt(Guid id,byte[] bytes)
    {
        lock(_gate)
        {
            if(bytes.Length>ReceiptBudget||!_reservedReceipts.Remove(id))
                throw new IOException("Receipt reservation unavailable.");
            try{Put(BenchSyncQueue.ReceiptName(id),bytes);}
            catch{_reservedReceipts.Add(id);throw;}
        }
    }
    private void Put(string filename,byte[] bytes)
    {
        lock(_gate)
        {
        if(!Fits(1,bytes.LongLength))throw new IOException("Water evidence spool full.");
        string target = Path.Combine(_root,filename);
        string temp = Path.Combine(_root,Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            using (var f = new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            { f.Write(bytes); f.Flush(flushToDisk:true); }
            File.Move(temp,target,overwrite:false);
            _bytes += bytes.Length; _files++;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    internal void BatchEvent(BenchBatchEvent value) => PutQueued("batch-timeline-"+value.EventId.ToString("N")+".json",value.EventId,
        JsonSerializer.SerializeToUtf8Bytes(value,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    internal void Nh3Event(Nh3EvidenceEvent value)=>PutQueued("nh3-evidence-"+value.EventId.ToString("N")+".json",value.EventId,
        JsonSerializer.SerializeToUtf8Bytes(value,BenchMediaManifest.Json));
    // Materialize validated historical journals as durable outbox events. Original bytes remain immutable.
    internal void StageAudits(Guid station)
    {
        if(station==Guid.Empty)return;
        lock(_gate)
        {
            var files=Directory.EnumerateFiles(_root).Take(100001).ToArray();
            if(files.Length>100000)throw new IOException("Audit scan limit.");
            void Stage(BenchAuditEvent e)
            {
                e.Validate();
                string name="audit-event-"+e.EventId.ToString("N")+".json";
                byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(e,BenchMediaManifest.Json);
                var path=Path.Combine(_root,name);
                if(File.Exists(path))
                {
                    if(!BenchCommandAudit.Read(path,32768).SequenceEqual(bytes))throw new IOException("Audit event changed.");
                    return;
                }
                PutQueued(name,e.EventId,bytes);
            }
            foreach(var file in files)
            {
                if(!BenchSyncQueue.TryFileId(Path.GetFileName(file),"command-",out var id))continue;
                var view=BenchCommandAudit.Inspect(_root,id,null);
                if(view.State=="legacy_unverified")continue;
                if(view.State=="invalid_journal")throw new IOException("Invalid command audit.");
                if(view.StationId!=station)continue;
                var a=BenchCommandAudit.Read(file,16384);
                Stage(BenchAuditEvent.Create(station,view.ConnectionId!.Value,view.BootId!,"command",id,0,view.AttemptAt!.Value,a));
                if(view.Outcome.HasValue)
                {
                    var o=BenchCommandAudit.Read(Path.Combine(_root,"outcome-"+id.ToString("N")+".json"),16384);
                    Stage(BenchAuditEvent.Create(station,view.ConnectionId.Value,view.BootId!,"command_outcome",id,0,view.OutcomeAt!.Value,o));
                }
            }
            var clean=files.Where(f=>Path.GetFileName(f).StartsWith("clean-audit-",StringComparison.Ordinal))
                .Select(f=>(Path:f,Bytes:BenchCommandAudit.Read(f,4096)))
                .Select(f=>(f.Path,f.Bytes,Event:BenchCommandAudit.Object(f.Bytes).Deserialize<BenchCleanEvent>(BenchMediaManifest.Json)
                    ??throw new FormatException("Cleaning event."))).ToArray();
            foreach(var group in clean.GroupBy(x=>x.Event.RunId))
            {
                var timeline=new BenchCleanTimeline();
                foreach(var row in group.OrderBy(x=>x.Event.Sequence))
                {
                    var e=row.Event;
                    if(Path.GetFileName(row.Path)!=BenchCleaningInventory.Name(e.RunId,e.Sequence))throw new FormatException("Cleaning filename.");
                    timeline.Apply(e);
                    if(e.StationId==station)Stage(BenchAuditEvent.Create(station,e.ConnectionId,e.BootId,"clean_status",e.RunId,e.Sequence,e.At,row.Bytes));
                }
            }
        }
    }
    internal int RecoverInterruptedBatches(Guid station,DateTimeOffset at)
    {
        if(station==Guid.Empty)return 0;
        lock(_gate)
        {
            var files=Directory.EnumerateFiles(_root).Take(100001).ToArray();
            if(files.Length>100000)throw new IOException("Recovery scan limit.");
            var events=new List<BenchBatchEvent>();
            foreach(var file in files.Where(f=>Path.GetFileName(f).StartsWith("batch-timeline-",StringComparison.Ordinal)))
            {
                var e=BenchCommandAudit.Object(BenchCommandAudit.Read(file,16384)).Deserialize<BenchBatchEvent>(BenchMediaManifest.Json)
                    ??throw new FormatException("Batch event.");
                if(!BenchSyncQueue.TryFileId(Path.GetFileName(file),"batch-timeline-",out var id)||id!=e.EventId)
                    throw new FormatException("Batch event identity.");
                if(e.StationId==station)events.Add(e);
            }
            int recovered=0;
            foreach(var group in events.GroupBy(e=>(e.BootId,e.BatchId)))
            {
                var rows=group.OrderBy(e=>e.Sequence).ToArray();
                var first=rows[0];var last=rows[^1];
                if(!WireProtocol.BatchId(first.BootId)||!WireProtocol.BatchId(first.BatchId)||first.ConnectionId==Guid.Empty||
                    first.Type!="start"||first.Mask is <1 or >7||rows.Length>512||
                    rows.Where((e,i)=>e.Sequence!=i+1||e.ConnectionId!=first.ConnectionId||
                        i>0&&(e.Type=="start"||rows[i-1].Type=="interrupted"||rows[i-1].Type=="status"&&rows[i-1].State is 2 or 3)).Any())
                    throw new FormatException("Invalid interrupted batch journal.");
                if(last.Type=="interrupted"||last.Type=="status"&&last.State is 2 or 3)continue;
                if(last.Sequence>=512)throw new IOException("No terminal observation capacity.");
                BatchEvent(new(station,Guid.NewGuid(),first.ConnectionId,first.BootId,first.BatchId,last.Sequence+1,
                    "interrupted",at,Reason:"kiosk_restart_no_replay"));
                recovered++;
            }
            return recovered;
        }
    }
    internal void Event(object value) => Put(Guid.NewGuid().ToString("N")+".json",JsonSerializer.SerializeToUtf8Bytes(value));
    internal void Command(Guid id,object value)=>ReserveCommand(id,JsonSerializer.SerializeToUtf8Bytes(value));
    private void ReserveCommand(Guid id,byte[] bytes)
    {
        lock(_gate)
        {
            if(id==Guid.Empty||bytes.Length>16384||HasCommand(id)||File.Exists(Path.Combine(_root,"outcome-"+id.ToString("N")+".json"))||
                !_reservedOutcomes.Add(id))throw new IOException("Duplicate or invalid command attempt.");
            try{Put("command-"+id.ToString("N")+".json",bytes);}
            catch{_reservedOutcomes.Remove(id);throw;}
        }
    }
    internal string BeginCommand(BenchCommandAttempt attempt)
    {
        BenchCommandAudit.Validate(attempt,attempt.RequestId);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(attempt,BenchMediaManifest.Json);
        ReserveCommand(attempt.RequestId,bytes);return BenchCommandAudit.Digest(bytes);
    }
    internal void FinishCommand(BenchCommandOutcome outcome)
    {
        lock(_gate)
        {
            var bytes=BenchCommandAudit.Read(Path.Combine(_root,"command-"+outcome.RequestId.ToString("N")+".json"),16384);
            var attempt=BenchCommandAudit.Object(bytes).Deserialize<BenchCommandAttempt>(BenchMediaManifest.Json)
                ??throw new IOException("Command attempt missing.");
            BenchCommandAudit.Validate(attempt,outcome.RequestId);
            BenchCommandAudit.Validate(outcome,attempt,BenchCommandAudit.Digest(bytes));
            WriteOutcome(outcome.RequestId,JsonSerializer.SerializeToUtf8Bytes(outcome,BenchMediaManifest.Json));
        }
    }
    internal void CleanEvent(BenchCleanEvent e)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(e,BenchMediaManifest.Json);
        if(bytes.Length>4096)throw new IOException("Cleaning evidence size.");
        Put(BenchCleaningInventory.Name(e.RunId,e.Sequence),bytes);
    }
    internal object CleaningRuns(Guid? live,int skip,int take,Guid? run)
    {
        lock(_gate)return BenchCleaningInventory.Query(_root,live,skip,take,run);
    }
    internal void Recovery(Guid id,object value)=>Put("recovery-"+id.ToString("N")+".json",JsonSerializer.SerializeToUtf8Bytes(value));
    internal bool HasCommand(Guid id)=>File.Exists(Path.Combine(_root,"command-"+id.ToString("N")+".json"));
    internal void Outcome(Guid id,object value)=>WriteOutcome(id,JsonSerializer.SerializeToUtf8Bytes(value));
    private void WriteOutcome(Guid id,byte[] bytes)
    {
        lock(_gate)
        {
            if(bytes.Length>OutcomeBudget||!_reservedOutcomes.Remove(id))throw new IOException("Command outcome reservation unavailable.");
            try{Put("outcome-"+id.ToString("N")+".json",bytes);}
            catch{_reservedOutcomes.Add(id);throw;}
        }
    }
    internal object? CommandStatus(Guid id,Guid? pending=null)
    {
        lock(_gate)
        {
            if(!HasCommand(id))return null;
            var v=BenchCommandAudit.Inspect(_root,id,pending);
            return new{requestId=v.RequestId,state=v.State,attempt=v.Attempt,outcome=v.Outcome,attemptSha256=v.AttemptSha256,
                reason=v.Reason,physicalCompletionConfirmed=false,commandsReplayed=false};
        }
    }
    internal object Commands(int skip,int take,Guid? pending)
    {
        lock(_gate)
        {
            if(skip<0||take is <1 or >50)throw new ArgumentException("Invalid page.");
            var files=Directory.EnumerateFiles(_root).Take(100001).ToArray();
            var ids=new HashSet<Guid>();var outcomes=new HashSet<Guid>();int invalid=files.Length>100000?1:0;
            foreach(var path in files.Take(100000))
            {
                var name=Path.GetFileName(path);
                if(name.StartsWith("command-",StringComparison.Ordinal))
                {if(!BenchSyncQueue.TryFileId(name,"command-",out var id)||!ids.Add(id))invalid++;}
                else if(name.StartsWith("outcome-",StringComparison.Ordinal))
                {if(!BenchSyncQueue.TryFileId(name,"outcome-",out var id)||!outcomes.Add(id))invalid++;}
            }
            invalid+=outcomes.Count(x=>!ids.Contains(x));
            var rows=ids.Select(id=>BenchCommandAudit.Inspect(_root,id,pending))
                .OrderByDescending(x=>x.AttemptAt).ThenBy(x=>x.RequestId).ToArray();
            invalid+=rows.Count(x=>x.State=="invalid_journal");
            var items=rows.Skip(skip).Take(take).Select(x=>new{requestId=x.RequestId,state=x.State,action=x.Action,
                stationId=x.StationId,connectionId=x.ConnectionId,bootId=x.BootId,sequence=x.Sequence,
                attemptAt=x.AttemptAt,outcomeAt=x.OutcomeAt,attemptSha256=x.AttemptSha256,reason=x.Reason,
                physicalCompletionConfirmed=false}).ToArray();
            return new{total=rows.Length,skip,take,inventoryComplete=invalid==0,invalidFiles=invalid,
                accepted=rows.Count(x=>x.State=="accepted"),rejected=rows.Count(x=>x.State=="rejected"),
                unknown=rows.Count(x=>x.State is "unknown" or "unknown_no_replay"),
                awaitingAck=rows.Count(x=>x.State=="awaiting_ack"),legacyUnverified=rows.Count(x=>x.State=="legacy_unverified"),
                items,physicalCompletionConfirmed=false,commandsReplayed=false};
        }
    }
    internal string Frame(ReceivedFrame frame,object link,BenchMediaIdentity? identity=null)
    {
        lock(_gate)
        {
        var eventId=Guid.NewGuid();
        var id=eventId.ToString("N");
        var manifest=identity is null?null:BenchMediaManifest.Create(eventId,frame,identity);
        // Manifest is the commit marker, written last. Orphan JPEG/raw metadata is never uploadable.
        var manifestBytes=manifest is null?null:JsonSerializer.SerializeToUtf8Bytes(manifest,BenchMediaManifest.Json);
        var rawBytes=JsonSerializer.SerializeToUtf8Bytes(new {
            kind="jpeg",mediaId=id,sha256=Convert.ToHexString(SHA256.HashData(frame.Bytes)).ToLowerInvariant(),
            frame.CaptureId,frame.Sequence,frame.Width,frame.Height,frame.Crc,frame.ReceivedAt,link,
            uploadState="pending",actualExposureVerified=false,chemicalAccuracyValidated=false
        });
        if(!Fits(manifestBytes is null?2:4,frame.Bytes.LongLength+rawBytes.LongLength+
            (manifestBytes?.LongLength??0)+(manifestBytes is null?0:ReceiptBudget)))
            throw new IOException("Water evidence spool full.");
        Put(id+".jpg",frame.Bytes);
        Put(id+".json",rawBytes);
        if(manifestBytes is not null)PutQueued("media-manifest-"+id+".json",eventId,manifestBytes);
        return id;
        }
    }
}
