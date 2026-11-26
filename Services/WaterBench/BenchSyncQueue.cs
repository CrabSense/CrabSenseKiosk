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
    IReadOnlyList<BenchSyncCandidate> Ready,string State);

internal sealed class BenchSyncQueue(EvidenceSpool spool,Guid station)
{
    private const int MaxMetadata=16384;
    private readonly object _gate=new();
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
    private sealed record Entry(BenchSyncCandidate Candidate,BenchBatchEvent? Batch,BenchMediaManifest? Media,bool Ack);
    private static bool ValidAck(BenchSyncCandidate c,BenchSyncReceipt r)=>
        r.EventId==c.EventId&&r.Kind==c.Kind&&r.LocalSha256==c.LocalSha256&&r.ReceiptId!=Guid.Empty&&r.RecordedAt!=default&&
        (c.Kind=="media"?(r.MediaId.HasValue&&r.MediaId!=Guid.Empty&&r.Sha256==c.ImageSha256):
            r.MediaId is null&&r.Sha256 is null);
    private Entry Load(string name)
    {
        bool isBatch=TryFileId(name,"batch-timeline-",out var id);
        if(!isBatch&&!TryFileId(name,"media-manifest-",out id))throw new IOException("Invalid spool filename.");
        var bytes=ReadBounded(PathFor(name),MaxMetadata);
        BenchBatchEvent? batch=null;BenchMediaManifest? media=null;BenchSyncCandidate c;
        if(isBatch)
        {
            batch=Parse<BenchBatchEvent>(bytes);
            if(batch.EventId!=id||!Identity(id,batch.StationId,batch.ConnectionId,batch.BootId,batch.BatchId,batch.ReceivedAt)||
                batch.Sequence is <1 or >512||batch.Type is not ("start" or "session" or "reaction" or "image" or "status" or "interrupted"))
                throw new IOException("Invalid batch event identity.");
            c=new(id,"batch",Hash(bytes),batch.StationId,batch.BootId,batch.BatchId,batch.ConnectionId,batch.ReceivedAt,batch.Sequence);
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
        if(ack&&!ValidAck(c,Parse<BenchSyncReceipt>(ReadBounded(receiptPath,EvidenceSpool.ReceiptBudget))))
            throw new IOException("Receipt does not match immutable evidence.");
        return new(c,batch,media,ack);
    }
    internal BenchSyncPlan Scan()
    {
        lock(_gate)return ScanLocked();
    }
    private BenchSyncPlan ScanLocked()
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
                if(!name.StartsWith("batch-timeline-",StringComparison.Ordinal)&&!name.StartsWith("media-manifest-",StringComparison.Ordinal))continue;
                try{entries.Add(Load(name));}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException){invalid++;}
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {return new(0,0,0,0,1,[],"storage_unavailable");}
        var ids=entries.Select(x=>x.Candidate.EventId).ToHashSet();
        invalid+=receipts.Count(id=>!ids.Contains(id));
        if(ids.Count!=entries.Count)invalid++;
        var current=entries.Where(x=>x.Candidate.StationId==station).ToArray();
        int foreign=entries.Count-current.Length;
        int pending=current.Count(x=>!x.Ack),acked=current.Length-pending;
        // Unreadable files cannot be assigned safely to a batch. Block rather than skip them.
        if(invalid>0)return new(pending,acked,foreign,0,invalid,[],"invalid_evidence");
        var ready=new List<BenchSyncCandidate>();int blocked=0;
        foreach(var group in current.GroupBy(x=>(x.Candidate.BootId,x.Candidate.BatchId)))
        {
            var rows=group.Where(x=>x.Batch!=null).OrderBy(x=>x.Batch!.Sequence).ToArray();
            var media=group.Where(x=>x.Media!=null).OrderBy(x=>x.Candidate.ReceivedAt).ThenBy(x=>x.Candidate.EventId).ToArray();
            var first=rows.FirstOrDefault();
            if(first is null||first.Batch!.Type!="start"||rows.Length>512||
                group.Any(x=>x.Candidate.ConnectionId!=first.Candidate.ConnectionId)||
                rows.Where((r,i)=>r.Batch!.Sequence!=i+1||(i>0&&r.Batch.Type=="start")).Any()||
                rows.SkipWhile(r=>r.Ack).Any(r=>r.Ack)||
                media.GroupBy(x=>(x.Media!.Analyte,x.Media.SessionId,x.Media.CaptureId)).Any(g=>g.Count()!=1))
            {blocked++;continue;}
            bool Matches(Entry r,Entry m)=>r.Batch!.Type=="image"&&r.Batch.Analyte==m.Media!.Analyte&&
                r.Batch.SessionId==m.Media.SessionId&&r.Batch.CaptureId==m.Media.CaptureId&&
                Math.Abs((r.Batch.ReceivedAt-m.Media.RequestedAt).TotalSeconds)<=2;
            if(media.Any(m=>rows.Count(r=>Matches(r,m))!=1||m.Ack&&!rows.Any(r=>r.Ack&&Matches(r,m))))
            {blocked++;continue;}
            // At most one candidate per batch: all currently committed timeline events first.
            var next=rows.FirstOrDefault(x=>!x.Ack)??media.FirstOrDefault(x=>!x.Ack);
            if(next!=null)ready.Add(next.Candidate);
        }
        return new(pending,acked,foreign,blocked,invalid,ready.OrderBy(x=>x.ReceivedAt).ThenBy(x=>x.EventId).ToArray(),
            blocked>0?"blocked_batches":pending>0?"pending":"idle");
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
        string name=(c.Kind=="batch"?"batch-timeline-":"media-manifest-")+c.EventId.ToString("N")+".json";
        var e=Load(name);
        if(e.Candidate!=c)throw new IOException("Evidence changed.");
        if(e.Batch!=null)return e.Batch;
        return e.Media!.UploadBody(ReadBounded(PathFor(e.Media.LocalFile),WireProtocol.MaxFrame));
    }
    // Called only by the future authenticated transport, never a public/local HTTP mutation.
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
                var existing=Parse<BenchSyncReceipt>(ReadBounded(path,EvidenceSpool.ReceiptBudget));
                if(!ValidAck(candidate,existing)||existing.ReceiptId!=server.ReceiptId||
                    existing.MediaId!=server.MediaId||existing.Sha256!=server.Sha256)
                    throw new IOException("Receipt changed.");
                return;
            }
            // A new timeline event may have arrived while a media POST was in flight.
            // A successful receipt for that media is still valid if its request prefix was acknowledged.
            var plan=ScanLocked();
            bool eligible=plan.Ready.Contains(candidate);
            if(!eligible&&candidate.Kind=="media"&&plan.InvalidFiles==0&&plan.BlockedBatches==0)
            {
                var m=Load("media-manifest-"+candidate.EventId.ToString("N")+".json");
                eligible=m.Candidate==candidate&&plan.Ready.Any(r=>r.Kind=="batch"&&r.BootId==candidate.BootId&&
                    r.BatchId==candidate.BatchId&&r.ConnectionId==candidate.ConnectionId);
            }
            if(!eligible)throw new IOException("Receipt candidate is not eligible.");
            _=PrepareLocked(candidate);
            var row=new BenchSyncReceipt(candidate.EventId,candidate.Kind,candidate.LocalSha256,server.ReceiptId,server.MediaId,server.Sha256,at);
            spool.PutReceipt(candidate.EventId,JsonSerializer.SerializeToUtf8Bytes(row,Json));
        }
    }
    internal object Snapshot()
    {
        var p=Scan();
        return new{p.State,p.Pending,p.Acknowledged,p.Foreign,p.BlockedBatches,p.InvalidFiles,readyBatches=p.Ready.Count,
            stationId=station,automaticUploadImplemented=false,rawEvidenceRetained=true,physicalCommandsReplayed=false};
    }
}
