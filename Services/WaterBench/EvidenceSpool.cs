using System.Security.Cryptography;
using System.Text.Json;

namespace CrabSenseKiosk.Services.WaterBench;

// Single writer (WaterBenchSession lock). No deleting or uploading evidence in P2.1.
internal sealed class EvidenceSpool
{
    private readonly string _root;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
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
        }
    }
    internal bool HasCapacity => _files + 3 <= _maxFiles && _bytes + 128*1024 <= _maxBytes;
    private void Put(string filename,byte[] bytes)
    {
        if (_files >= _maxFiles || bytes.LongLength > _maxBytes - _bytes)
            throw new IOException("Water evidence spool full.");
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
    internal void Event(object value) => Put(Guid.NewGuid().ToString("N")+".json",JsonSerializer.SerializeToUtf8Bytes(value));
    internal void Command(Guid id,object value) => Put("command-"+id.ToString("N")+".json",JsonSerializer.SerializeToUtf8Bytes(value));
    internal string Frame(ReceivedFrame frame,object link)
    {
        var id = Guid.NewGuid().ToString("N");
        Put(id+".jpg",frame.Bytes);
        Put(id+".json",JsonSerializer.SerializeToUtf8Bytes(new {
            kind="jpeg", mediaId=id, sha256=Convert.ToHexString(SHA256.HashData(frame.Bytes)).ToLowerInvariant(),
            frame.CaptureId,frame.Sequence,frame.Width,frame.Height,frame.Crc,frame.ReceivedAt,link,
            uploadState="pending", actualExposureVerified=false, chemicalAccuracyValidated=false
        }));
        return id;
    }
}
