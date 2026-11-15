using System.Diagnostics;
namespace CrabSenseKiosk.Services;

public sealed class KioskMonitoringMetrics(IConfiguration config)
{
    public sealed record ResourceSnapshot(double? ProcessCpuPercent,long? ProcessMemoryBytes,long? DiskTotalBytes,long? DiskFreeBytes,string? SoftwareVersion,
        DateTimeOffset? EdgeSyncedAt,DateTimeOffset? RecordingCheckedAt,DateTimeOffset? UploadCheckedAt);
    private readonly object gate=new();
    private long edgeTicks,recordTicks,uploadTicks;
    private long priorClock;
    private double priorCpu;
    public void MarkEdgeSync()=>Interlocked.Exchange(ref edgeTicks,DateTime.UtcNow.Ticks);
    public void MarkRecordingCheck()=>Interlocked.Exchange(ref recordTicks,DateTime.UtcNow.Ticks);
    public void MarkUploadCheck()=>Interlocked.Exchange(ref uploadTicks,DateTime.UtcNow.Ticks);
    private static DateTimeOffset? Time(long ticks)=>ticks==0?null:new DateTimeOffset(ticks,TimeSpan.Zero);
    public ResourceSnapshot Snapshot(){
        double? cpu=null;long? memory=null,total=null,free=null;
        try{
            using var process=Process.GetCurrentProcess();
            memory=process.WorkingSet64;
            lock(gate){
                var clock=Stopwatch.GetTimestamp();var current=process.TotalProcessorTime.TotalMilliseconds;
                if(priorClock!=0&&clock>priorClock){var elapsed=Stopwatch.GetElapsedTime(priorClock,clock).TotalMilliseconds;
                    cpu=Math.Clamp((current-priorCpu)/elapsed/Math.Max(1,Environment.ProcessorCount)*100,0,100);}
                priorClock=clock;priorCpu=current;
            }
        }catch(InvalidOperationException){}
        catch(System.ComponentModel.Win32Exception){}
        try{
            var path=Path.GetFullPath(config["CameraRecording:SpoolDirectory"]??AppContext.BaseDirectory);
            var root=Path.GetPathRoot(path);if(root!=null){var drive=new DriveInfo(root);if(drive.IsReady){total=drive.TotalSize;free=drive.AvailableFreeSpace;}}
        }catch(IOException){}
        catch(UnauthorizedAccessException){}
        catch(ArgumentException){}
        return new(cpu,memory,total,free,typeof(KioskMonitoringMetrics).Assembly.GetName().Version?.ToString(),
            Time(Interlocked.Read(ref edgeTicks)),Time(Interlocked.Read(ref recordTicks)),Time(Interlocked.Read(ref uploadTicks)));
    }
}
