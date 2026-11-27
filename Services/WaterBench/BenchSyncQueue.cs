using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrabSenseKiosk.Services.WaterBench;

// Disk-backed outbox: no network or UART access. A single queue per spool owns receipt writes.
internal sealed record BenchSyncCandidate(Guid EventId,string Kind,string LocalSha256,Guid StationId,
    string BootId,string BatchId,Guid ConnectionId,DateTimeOffset ReceivedAt,int? Sequence,
    string? ImageSha256=null);
internal sealed record BenchSyncServerReceipt(Guid EventId,Guid ReceiptId,Guid? MediaId=null,string? Sha256=null);
internal sealed record BenchSyncReceipt(Guid EventId,string Kind,string LocalSha256,Guid ReceiptId,Guid? MediaId,
    string? Sha256,DateTimeOffset RecordedAt);
internal sealed record BenchSyncPlan(int Pending,int Acknowledged,int Foreign,int BlockedBatches,int InvalidFiles,
    IReadOnlyList<BenchSyncCandidate> Ready,string State)
{
    internal IReadOnlyList<BenchSyncCandidate> Acknowledgeable {get;init;}=[];
    internal IReadOnlySet<Guid> ReceiptIds {get;init;}=new HashSet<Guid>();
    internal IReadOnlySet<Guid> MediaIds {get;init;}=new HashSet<Guid>();
}

internal sealed class BenchSyncQueue(EvidenceSpool spool,Guid station)
{
    private const int MaxMetadata=16384;
    private readonly object _gate=new();
    private string _transportState="disabled";
    private DateTimeOffset? _lastAttemptAt,_lastReceiptAt;
    private int? _lastHttpStatus;
    private int _deferredBatches;
    private DateTimeOffset? _nextRetryAt,_globalRetryUntil;
    internal string RetryDirectory=>Path.Combine(spool.Root,"sync-retry");
    private IReadOnlyList<BenchRetryEntry> _retryEntries=[];
    internal void RetryStatus(int deferred,DateTimeOffset? next,DateTimeOffset? global,IReadOnlyList<BenchRetryEntry>? entries=null)
    {
        lock(_gate){_deferredBatches=deferred;_nextRetryAt=next;_globalRetryUntil=global;_retryEntries=entries?.ToArray()??[];}
    }
    internal bool HasVerifiedReceipt(Guid id,string kind,string localSha256)
    {
        lock(_gate)
        {
            try
            {
                string prefix=kind switch{"batch"=>"batch-timeline-","media"=>"media-manifest-","nh3"=>"nh3-evidence-","audit"=>"audit-event-",_=>throw new IOException("Unknown sync kind.")};
                var e=Load(prefix+id.ToString("N")+".json");
                return e.Ack&&e.Candidate.StationId==station&&e.Candidate.LocalSha256==localSha256;
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException){return false;}
        }
    }
    internal void TransportStatus(string state,DateTimeOffset? attempt=null,DateTimeOffset? receipt=null,int? httpStatus=null)
    {
        lock(_gate)
        {
            _transportState=state;
            if(attempt.HasValue)_lastAttemptAt=attempt;
            if(receipt.HasValue)_lastReceiptAt=receipt;
            _lastHttpStatus=httpStatus;
        }
    }
    private static JsonSerializerOptions Json=>BenchMediaManifest.Json;
    internal static string ReceiptName(Guid id)=>"sync-receipt-"+id.ToString("N")+".json";
    internal static bool TryFileId(string name,string prefix,out Guid id)
    {
        id=Guid.Empty;
        return name.Length==prefix.Length+37&&name.StartsWith(prefix,StringComparison.Ordinal)&&
            name.EndsWith(".json",StringComparison.Ordinal)&&Guid.TryParseExact(name.AsSpan(prefix.Length,32),"N",out id)&&
            id!=Guid.Empty&&name==prefix+id.ToString("N")+".json";
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool Digest(string? s)=>s is not null&&Regex.IsMatch(s,@"\A[0-9a-f]{64}\z",RegexOptions.CultureInvariant);
    private static byte[] ReadBounded(string path,int max)
    {
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked spool file.");
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length<1||stream.Length>max)throw new IOException("Spool file size.");
        var bytes=new byte[checked((int)stream.Length)];stream.ReadExactly(bytes);
        if(stream.ReadByte()!=-1)throw new IOException("Spool file changed.");
        return bytes;
    }
    private string PathFor(string name)=>Path.Combine(spool.Root,name);
    private static T Parse<T>(byte[] bytes) where T:class=>JsonSerializer.Deserialize<T>(bytes,Json)
        ??throw new IOException("Invalid spool JSON.");
    private static bool Identity(Guid id,Guid station,Guid connection,string boot,string batch,DateTimeOffset at)=>
        id!=Guid.Empty&&station!=Guid.Empty&&connection!=Guid.Empty&&WireProtocol.BatchId(boot)&&WireProtocol.BatchId(batch)&&at!=default;
    private sealed record Entry(BenchSyncCandidate Candidate,BenchBatchEvent? Batch,BenchMediaManifest? Media,bool Ack,Nh3EvidenceEvent? Nh3=null,BenchSyncReceipt? Receipt=null,BenchAuditEvent? Audit=null);
    private static bool ValidAck(BenchSyncCandidate c,BenchSyncReceipt r)=>
        r.EventId==c.EventId&&r.Kind==c.Kind&&r.LocalSha256==c.LocalSha256&&r.ReceiptId!=Guid.Empty&&r.RecordedAt!=default&&
        (c.Kind=="media"?(r.MediaId.HasValue&&r.MediaId!=Guid.Empty&&r.Sha256==c.ImageSha256):
            r.MediaId is null&&r.Sha256 is null);
    private Entry Load(string name)
    {
        bool isBatch=TryFileId(name,"batch-timeline-",out var id);
        bool isNh3=!isBatch&&TryFileId(name,"nh3-evidence-",out id);
        if(isBatch)_=TryFileId(name,"batch-timeline-",out id);
        bool isAudit=!isBatch&&!isNh3&&TryFileId(name,"audit-event-",out id);
        if(!isBatch&&!isNh3&&!isAudit&&!TryFileId(name,"media-manifest-",out id))throw new IOException("Invalid spool filename.");
        var bytes=ReadBounded(PathFor(name),isAudit?32768:MaxMetadata);
        BenchBatchEvent? batch=null;BenchMediaManifest? media=null;Nh3EvidenceEvent? nh3=null;BenchAuditEvent? audit=null;BenchSyncCandidate c;
        if(isBatch)
        {
            batch=Parse<BenchBatchEvent>(bytes);
            if(batch.EventId!=id||!Identity(id,batch.StationId,batch.ConnectionId,batch.BootId,batch.BatchId,batch.ReceivedAt)||
                batch.Sequence is <1 or >512||batch.Type is not ("start" or "session" or "reaction" or "image" or "status" or "interrupted"))
                throw new IOException("Invalid batch event identity.");
            c=new(id,"batch",Hash(bytes),batch.StationId,batch.BootId,batch.BatchId,batch.ConnectionId,batch.ReceivedAt,batch.Sequence);
        }
        else if(isNh3)
        {
            nh3=Parse<Nh3EvidenceEvent>(bytes);Nh3EvidencePolicy.Validate(nh3);
            if(nh3.EventId!=id)throw new IOException("Invalid NH3 event identity.");
            c=new(id,"nh3",Hash(bytes),nh3.StationId,nh3.BootId,nh3.BatchId,nh3.ConnectionId,nh3.ReceivedAt,null);
        }
        else if(isAudit)
        {
            audit=Parse<BenchAuditEvent>(bytes);audit.Validate();
            if(audit.EventId!=id)throw new IOException("Audit event identity.");
            c=new(id,"audit",Hash(bytes),audit.StationId,audit.BootId,audit.StreamId,audit.ConnectionId,audit.At,audit.Order);
        }
        else
        {
            media=Parse<BenchMediaManifest>(bytes);
            if(media.EventId!=id||!Identity(id,media.StationId,media.ConnectionId,media.BootId,media.BatchId,media.ReceivedAt)||
                media.Analyte is not ("NO2" or "NO3" or "NH3")||
                !(media.Analyte=="NH3"?WireProtocol.BatchId(media.SessionId):media.SessionId==media.BatchId+(media.Analyte=="NO2"?"-1":"-2"))||
                media.CaptureId is <1 or >65536||media.SizeBytes is <4 or >WireProtocol.MaxFrame||
                media.LocalFile!=id.ToString("N")+".jpg"||!Digest(media.Sha256)||media.UploadState!="pending"||
                media.ActualExposureVerified||media.ChemicalAccuracyValidated||media.RequestedAt==default||
                Math.Abs((media.ReceivedAt-media.RequestedAt).TotalSeconds)>60||
                !WireProtocol.HardwareId(media.MasterHardwareId)||!WireProtocol.HardwareId(media.SlaveHardwareId)||
                media.MasterHardwareId==media.SlaveHardwareId)
                throw new IOException("Invalid media manifest identity.");
            // Check presence and size here; full bytes/hash/decode are checked before transport.
            var jpg=new FileInfo(PathFor(media.LocalFile));
            if(!jpg.Exists||jpg.Length!=media.SizeBytes||(jpg.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new IOException("Missing or invalid JPEG file.");
            c=new(id,"media",Hash(bytes),media.StationId,media.BootId,media.BatchId,media.ConnectionId,media.ReceivedAt,null,media.Sha256);
        }
        var receiptPath=PathFor(ReceiptName(id));bool ack=File.Exists(receiptPath);
        var receipt=ack?Parse<BenchSyncReceipt>(ReadBounded(receiptPath,EvidenceSpool.ReceiptBudget)):null;
        if(receipt!=null&&!ValidAck(c,receipt))throw new IOException("Receipt does not match immutable evidence.");
        return new(c,batch,media,ack,nh3,receipt,audit);
    }
    internal BenchSyncPlan Scan()
    {
        lock(_gate)return ScanLocked();
    }
    private BenchSyncPlan ScanLocked(Action<Entry[],HashSet<(string BootId,string BatchId)>>? inspect=null)
    {
        if(station==Guid.Empty)return new(0,0,0,0,0,[],"station_not_configured");
        var entries=new List<Entry>();var receipts=new HashSet<Guid>();int invalid=0,seen=0;
        try
        {
            foreach(string file in Directory.EnumerateFiles(spool.Root))
            {
                // Count every file, including raw evidence; stop at the hard spool ceiling.
                if(++seen>100000)return new(0,0,0,0,1,[],"scan_limit_exceeded");
                var name=Path.GetFileName(file);
                if(name.StartsWith("sync-receipt-",StringComparison.Ordinal))
                {
                    if(!TryFileId(name,"sync-receipt-",out var receiptId)||!receipts.Add(receiptId))invalid++;
                    continue;
                }
                if(!name.StartsWith("batch-timeline-",StringComparison.Ordinal)&&!name.StartsWith("media-manifest-",StringComparison.Ordinal)&&
                    !name.StartsWith("nh3-evidence-",StringComparison.Ordinal)&&!name.StartsWith("audit-event-",StringComparison.Ordinal))continue;
                try{entries.Add(Load(name));}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException){invalid++;}
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {return new(0,0,0,0,1,[],"storage_unavailable");}
        var ids=entries.Select(x=>x.Candidate.EventId).ToHashSet();
        invalid+=receipts.Count(id=>!ids.Contains(id));
        if(ids.Count!=entries.Count)invalid++;
        var savedReceipts=entries.Where(x=>x.Receipt!=null).Select(x=>x.Receipt!).ToArray();
        var receiptIds=savedReceipts.Select(x=>x.ReceiptId).ToHashSet();
        var mediaIds=savedReceipts.Where(x=>x.MediaId.HasValue).Select(x=>x.MediaId!.Value).ToHashSet();
        if(receiptIds.Count!=savedReceipts.Length||mediaIds.Count!=savedReceipts.Count(x=>x.MediaId.HasValue))invalid++;
        var current=entries.Where(x=>x.Candidate.StationId==station).ToArray();
        int foreign=entries.Count-current.Length;
        int pending=current.Count(x=>!x.Ack),acked=current.Length-pending;
        // Unreadable files cannot be assigned safely to a batch. Block rather than skip them.
        var blockedKeys=new HashSet<(string BootId,string BatchId)>();
        if(invalid>0){inspect?.Invoke(current,blockedKeys);return new(pending,acked,foreign,0,invalid,[],"invalid_evidence");}
        var ready=new List<BenchSyncCandidate>();var ackable=new List<BenchSyncCandidate>();int blocked=0;
        foreach(var group in current.GroupBy(x=>(x.Candidate.BootId,x.Candidate.BatchId)))
        {
            if(group.Any(x=>x.Audit!=null))
            {
                var audits=group.OrderBy(x=>x.Audit?.Order).ToArray();
                var firstAudit=audits[0].Audit;
                if(firstAudit==null||audits.Any(x=>x.Audit==null||x.Audit.JournalId!=firstAudit.JournalId||
                    x.Audit.ConnectionId!=firstAudit.ConnectionId)||audits.SkipWhile(x=>x.Ack).Any(x=>x.Ack)||
                    (firstAudit.Kind=="clean_status"?audits.Where((x,i)=>x.Audit!.Kind!="clean_status"||x.Audit.Sequence!=i+1).Any():
                    firstAudit.Kind!="command"||audits.Length>2||audits.Length==2&&audits[1].Audit!.Kind!="command_outcome"))
                {blocked++;blockedKeys.Add(group.Key);continue;}
                var auditNext=audits.FirstOrDefault(x=>!x.Ack);
                if(auditNext!=null){ready.Add(auditNext.Candidate);ackable.Add(auditNext.Candidate);}
                continue;
            }
            var rows=group.Where(x=>x.Batch!=null).OrderBy(x=>x.Batch!.Sequence).ToArray();
            var media=group.Where(x=>x.Media!=null).OrderBy(x=>x.Candidate.ReceivedAt).ThenBy(x=>x.Candidate.EventId).ToArray();
            var nh3=group.Where(x=>x.Nh3!=null).OrderBy(x=>x.Nh3!.Type=="sample"?0:1).ThenBy(x=>x.Candidate.ReceivedAt).ToArray();
            var first=rows.FirstOrDefault();
            if(first is null||first.Batch!.Type!="start"||rows.Length>512||
                group.Any(x=>x.Candidate.ConnectionId!=first.Candidate.ConnectionId)||
                rows.Where((r,i)=>r.Batch!.Sequence!=i+1||(i>0&&r.Batch.Type=="start")).Any()||
                rows.SkipWhile(r=>r.Ack).Any(r=>r.Ack)||
                media.GroupBy(x=>(x.Media!.Analyte,x.Media.SessionId,x.Media.CaptureId)).Any(g=>g.Count()!=1)||
                nh3.GroupBy(x=>(x.Nh3!.SessionId,x.Nh3.Type)).Any(g=>g.Count()!=1))
            {blocked++;blockedKeys.Add(group.Key);continue;}
            bool Matches(Entry r,Entry m)=>r.Batch!.Type=="image"&&r.Batch.Analyte==m.Media!.Analyte&&
                r.Batch.SessionId==m.Media.SessionId&&r.Batch.CaptureId==m.Media.CaptureId&&
                Math.Abs((r.Batch.ReceivedAt-m.Media.RequestedAt).TotalSeconds)<=2;
            if(media.Any(m=>rows.Count(r=>Matches(r,m))!=1||m.Ack&&!rows.Any(r=>r.Ack&&Matches(r,m))))
            {blocked++;blockedKeys.Add(group.Key);continue;}
            bool SessionMatches(Entry row,Nh3EvidenceEvent n)=>row.Batch!.Type=="session"&&row.Batch.Analyte=="NH3"&&
                row.Batch.SessionId==n.SessionId&&n.ReceivedAt>=row.Batch.ReceivedAt.AddSeconds(-2);
            bool ImageMatches(Entry row,Nh3EvidenceEvent n)=>row.Batch!.Type=="image"&&row.Batch.Analyte=="NH3"&&
                row.Batch.SessionId==n.SessionId&&row.Batch.CaptureId==n.Image?.CaptureId&&
                Math.Abs((row.Batch.ReceivedAt-n.ReceivedAt).TotalSeconds)<=2;
            if(nh3.Any(n=>rows.Count(r=>SessionMatches(r,n.Nh3!))!=1||
                (n.Nh3!.Type=="image_link"&&rows.Count(r=>ImageMatches(r,n.Nh3))!=1)||
                (n.Ack&&(!rows.Any(r=>r.Ack&&SessionMatches(r,n.Nh3))||
                    (n.Nh3.Type=="image_link"&&!rows.Any(r=>r.Ack&&ImageMatches(r,n.Nh3)))))))
            {blocked++;blockedKeys.Add(group.Key);continue;}
            // One timeline candidate per batch. After the timeline prefix is acknowledged,
            // expose at most one candidate of each optional stream so disabled NH3 cannot block JPEGs.
            var next=rows.FirstOrDefault(x=>!x.Ack);
            var n=nh3.FirstOrDefault(x=>!x.Ack);
            var m=media.FirstOrDefault(x=>!x.Ack);
            if(next!=null){ready.Add(next.Candidate);ackable.Add(next.Candidate);}
            else
            {
                if(n!=null)ready.Add(n.Candidate);
                if(m!=null)ready.Add(m.Candidate);
            }
            // A timeline event may be appended while NH3/media is in flight.
            // Accept only the exact first pending optional event with acknowledged prerequisites.
            if(n!=null&&rows.Any(r=>r.Ack&&SessionMatches(r,n.Nh3!))&&
                (n.Nh3!.Type!="image_link"||rows.Any(r=>r.Ack&&ImageMatches(r,n.Nh3))))ackable.Add(n.Candidate);
            if(m!=null&&rows.Any(r=>r.Ack&&Matches(r,m)))ackable.Add(m.Candidate);
        }
        inspect?.Invoke(current,blockedKeys);
        return new(pending,acked,foreign,blocked,invalid,ready.OrderBy(x=>x.ReceivedAt).ThenBy(x=>x.EventId).ToArray(),
            blocked>0?"blocked_batches":pending>0?"pending":"idle")
            {Acknowledgeable=ackable,ReceiptIds=receiptIds,MediaIds=mediaIds};
    }
    internal object Prepare(BenchSyncCandidate candidate)
    {
        lock(_gate)
        {
            if(!ScanLocked().Ready.Contains(candidate))throw new IOException("Candidate is no longer eligible.");
            return PrepareLocked(candidate);
        }
    }
    private object PrepareLocked(BenchSyncCandidate c)
    {
        string prefix=c.Kind switch{"batch"=>"batch-timeline-","media"=>"media-manifest-","nh3"=>"nh3-evidence-","audit"=>"audit-event-",_=>throw new IOException("Unknown sync kind.")};
        string name=prefix+c.EventId.ToString("N")+".json";
        var e=Load(name);
        if(e.Candidate!=c)throw new IOException("Evidence changed.");
        if(e.Audit!=null)return e.Audit;
        if(e.Batch!=null)return e.Batch;
        if(e.Nh3!=null)return e.Nh3;
        return e.Media!.UploadBody(ReadBounded(PathFor(e.Media.LocalFile),WireProtocol.MaxFrame));
    }
    // Called only by authenticated transport, never a public/local HTTP mutation.
    internal void Accept(BenchSyncCandidate candidate,BenchSyncServerReceipt server,DateTimeOffset at)
    {
        lock(_gate)
        {
            if(candidate.StationId!=station||server.EventId!=candidate.EventId||server.ReceiptId==Guid.Empty||at==default||
                (candidate.Kind=="media"?(server.MediaId is null||server.MediaId==Guid.Empty||server.Sha256!=candidate.ImageSha256):
                    server.MediaId!=null||server.Sha256!=null))
                throw new IOException("Server receipt mismatch.");
            string name=ReceiptName(candidate.EventId);var path=PathFor(name);
            if(File.Exists(path))
            {
                if(!HasVerifiedReceipt(candidate.EventId,candidate.Kind,candidate.LocalSha256))
                    throw new IOException("Receipt evidence changed.");
                var existing=Parse<BenchSyncReceipt>(ReadBounded(path,EvidenceSpool.ReceiptBudget));
                if(!ValidAck(candidate,existing)||existing.ReceiptId!=server.ReceiptId||
                    existing.MediaId!=server.MediaId||existing.Sha256!=server.Sha256)
                    throw new IOException("Receipt changed.");
                return;
            }
            var plan=ScanLocked();
            if(!plan.Acknowledgeable.Contains(candidate))throw new IOException("Receipt candidate is not eligible.");
            if(plan.ReceiptIds.Contains(server.ReceiptId)||server.MediaId is Guid mediaId&&plan.MediaIds.Contains(mediaId))
                throw new IOException("Server receipt or media ID already belongs to another event.");
            _=PrepareLocked(candidate);
            var row=new BenchSyncReceipt(candidate.EventId,candidate.Kind,candidate.LocalSha256,server.ReceiptId,server.MediaId,server.Sha256,at);
            spool.PutReceipt(candidate.EventId,JsonSerializer.SerializeToUtf8Bytes(row,Json));
        }
    }
    private static object Counts(IEnumerable<Entry> entries)
    {
        var rows=entries.ToArray();
        return new{total=rows.Length,pending=rows.Count(x=>!x.Ack),acknowledged=rows.Count(x=>x.Ack)};
    }
    private object BatchSummary(IGrouping<(string BootId,string BatchId),Entry> g,
        BenchSyncPlan plan,HashSet<(string BootId,string BatchId)> blocked,DateTimeOffset now)
    {
        var rows=g.ToArray();var pending=rows.Count(x=>!x.Ack);
        var last=rows.Where(x=>x.Batch!=null).OrderByDescending(x=>x.Batch!.Sequence).FirstOrDefault()?.Batch;
        var retry=_retryEntries.FirstOrDefault(x=>x.BootId==g.Key.BootId&&x.BatchId==g.Key.BatchId);
        var state=plan.InvalidFiles>0?"inventory_invalid":blocked.Contains(g.Key)?"blocked":
            pending==0?"local_receipts_complete":_globalRetryUntil>now?"global_retry_wait":
            retry?.NextAt>now?"batch_retry_wait":"pending";
        return new{g.Key.BootId,g.Key.BatchId,state,total=rows.Length,pending,acknowledged=rows.Length-pending,
            timeline=Counts(rows.Where(x=>x.Candidate.Kind=="batch")),media=Counts(rows.Where(x=>x.Candidate.Kind=="media")),
            nh3=Counts(rows.Where(x=>x.Candidate.Kind=="nh3")),audit=Counts(rows.Where(x=>x.Candidate.Kind=="audit")),
            lastObservedAt=rows.Max(x=>x.Candidate.ReceivedAt),
            lastLocalReceiptAt=rows.Where(x=>x.Receipt!=null).Select(x=>(DateTimeOffset?)x.Receipt!.RecordedAt).Max(),
            timelineTerminal=last?.Type=="interrupted"||last?.Type=="status"&&last.State is 2 or 3,
            retry=retry is null?null:new{retry.EventId,retry.Kind,retry.Attempts,retry.NextAt,retry.Reason},
            physicalCompletionVerified=false,chemicalResultValidated=false};
    }
    internal object Batches(int skip,int take)
    {
        lock(_gate)
        {
            Entry[] rows=[];HashSet<(string BootId,string BatchId)> blocked=[];
            var p=ScanLocked((e,b)=>{rows=e;blocked=b;});
            var groups=rows.Where(x=>x.Audit==null).GroupBy(x=>(x.Candidate.BootId,x.Candidate.BatchId))
                .OrderByDescending(g=>g.Max(x=>x.Candidate.ReceivedAt)).ThenBy(g=>g.Key.BootId).ThenBy(g=>g.Key.BatchId).ToArray();
            return new{stationId=station,p.State,inventoryComplete=p.InvalidFiles==0&&p.State!="station_not_configured",
                total=groups.Length,skip,take,items=groups.Skip(skip).Take(take).Select(g=>BatchSummary(g,p,blocked,DateTimeOffset.UtcNow)).ToArray(),
                receiptBasis="local_validated_receipts_not_live_backend_check"};
        }
    }
    internal object? BatchReceipts(string boot,string batch)
    {
        lock(_gate)
        {
            Entry[] rows=[];HashSet<(string BootId,string BatchId)> blocked=[];
            var p=ScanLocked((e,b)=>{rows=e;blocked=b;});
            var g=rows.Where(x=>x.Audit==null&&x.Candidate.BootId==boot&&x.Candidate.BatchId==batch)
                .GroupBy(x=>(x.Candidate.BootId,x.Candidate.BatchId)).SingleOrDefault();
            if(g is null)return p.InvalidFiles>0?new{state=p.State,inventoryComplete=false}:null;
            var items=g.OrderBy(x=>x.Batch?.Sequence??int.MaxValue).ThenBy(x=>x.Candidate.ReceivedAt).ThenBy(x=>x.Candidate.EventId).Take(518)
                .Select(x=>new{x.Candidate.EventId,x.Candidate.Kind,x.Candidate.ConnectionId,x.Candidate.Sequence,x.Candidate.ReceivedAt,
                    x.Candidate.LocalSha256,sessionId=x.Batch?.SessionId??x.Media?.SessionId??x.Nh3?.SessionId,
                    captureId=x.Batch?.CaptureId??x.Media?.CaptureId??x.Nh3?.Image?.CaptureId,
                    state=x.Ack?"acknowledged":"pending",receiptId=x.Receipt?.ReceiptId,mediaId=x.Receipt?.MediaId,
                    sha256=x.Candidate.ImageSha256,recordedAt=x.Receipt?.RecordedAt}).ToArray();
            return new{stationId=station,summary=BatchSummary(g,p,blocked,DateTimeOffset.UtcNow),
                inventoryComplete=p.InvalidFiles==0&&g.Count()<=517,items,
                receiptBasis="local_validated_receipts_not_live_backend_check",jpegContentRehashed=false};
        }
    }
    internal object Snapshot(bool batchSyncEnabled=false,bool mediaSyncEnabled=false,bool nh3SyncEnabled=false)
    {
        lock(_gate)
        {
            Entry[] rows=[];
            var p=ScanLocked((e,b)=>rows=e);
            return new{p.State,p.Pending,p.Acknowledged,p.Foreign,p.BlockedBatches,p.InvalidFiles,readyBatches=p.Ready.Select(x=>(x.BootId,x.BatchId)).Distinct().Count(),
                timeline=Counts(rows.Where(x=>x.Candidate.Kind=="batch")),media=Counts(rows.Where(x=>x.Candidate.Kind=="media")),
                nh3=Counts(rows.Where(x=>x.Candidate.Kind=="nh3")),audit=Counts(rows.Where(x=>x.Candidate.Kind=="audit")),
                durableLastReceiptAt=rows.Where(x=>x.Receipt!=null).Select(x=>(DateTimeOffset?)x.Receipt!.RecordedAt).Max(),
                inventoryComplete=p.InvalidFiles==0&&p.State!="station_not_configured",storage=spool.CapacitySnapshot(),
                stationId=station,automaticUploadImplemented=true,batchSyncEnabled,mediaSyncEnabled=batchSyncEnabled&&mediaSyncEnabled,nh3SyncEnabled=batchSyncEnabled&&nh3SyncEnabled,
                transportState=batchSyncEnabled?_transportState:"disabled",lastAttemptAt=_lastAttemptAt,lastReceiptAt=_lastReceiptAt,
                lastHttpStatus=_lastHttpStatus,deferredBatches=_deferredBatches,nextRetryAt=_nextRetryAt,globalRetryUntil=_globalRetryUntil,
                rawEvidenceRetained=true,physicalCommandsReplayed=false};
        }
    }
}
