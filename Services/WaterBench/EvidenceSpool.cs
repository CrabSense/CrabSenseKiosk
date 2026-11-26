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
            if((BenchSyncQueue.TryFileId(name,"batch-timeline-",out var id)||BenchSyncQueue.TryFileId(name,"media-manifest-",out id)||BenchSyncQueue.TryFileId(name,"nh3-evidence-",out id))&&
                !File.Exists(Path.Combine(_root,BenchSyncQueue.ReceiptName(id))))_reservedReceipts.Add(id);
        }
    }
    private bool Fits(int files,long bytes)=>_files+(long)_reservedReceipts.Count+files<=_maxFiles&&
        _bytes+(long)_reservedReceipts.Count*ReceiptBudget+bytes<=_maxBytes;
    internal bool HasCapacity {get{lock(_gate)return Fits(4,128*1024+ReceiptBudget);}}
    internal object CapacitySnapshot()
    {
        lock(_gate)return new{usedBytes=_bytes,usedFiles=_files,maxBytes=_maxBytes,maxFiles=_maxFiles,
            reservedReceiptFiles=_reservedReceipts.Count,reservedReceiptBytes=(long)_reservedReceipts.Count*ReceiptBudget,
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
    internal void Event(object value) => Put(Guid.NewGuid().ToString("N")+".json",JsonSerializer.SerializeToUtf8Bytes(value));
    internal void Command(Guid id,object value) => Put("command-"+id.ToString("N")+".json",JsonSerializer.SerializeToUtf8Bytes(value));
    internal bool HasCommand(Guid id)=>File.Exists(Path.Combine(_root,"command-"+id.ToString("N")+".json"));
    internal void Outcome(Guid id,object value)=>Put("outcome-"+id.ToString("N")+".json",JsonSerializer.SerializeToUtf8Bytes(value));
    internal object? CommandStatus(Guid id)
    {
        var attempt=Path.Combine(_root,"command-"+id.ToString("N")+".json");
        var outcome=Path.Combine(_root,"outcome-"+id.ToString("N")+".json");
        if(!File.Exists(attempt))return null;
        static JsonElement Read(string path)
        {
            if(new FileInfo(path).Length>16384)throw new IOException("Command journal size.");
            using var doc=JsonDocument.Parse(File.ReadAllBytes(path));return doc.RootElement.Clone();
        }
        return new {requestId=id,attempt=Read(attempt),outcome=File.Exists(outcome)?Read(outcome):(JsonElement?)null,
            physicalCompletionConfirmed=false};
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
