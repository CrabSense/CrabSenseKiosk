using System.Globalization;
using System.Text.Json;

namespace CrabSenseKiosk.Services.WaterBench;

public sealed record BenchScheduleSettings(bool Enabled,string[] Times,int GraceMinutes=5);
public sealed record BenchScheduleApproval(Guid SlotId);
internal sealed record ScheduleSlot(Guid Id,DateTimeOffset DueUtc,DateTimeOffset ExpiresUtc,string State,string? Reason=null);
internal sealed record ScheduleState(int Version,BenchScheduleSettings Settings,DateTimeOffset ChangedUtc,
    DateTimeOffset? LastDueUtc,ScheduleSlot? Slot);

internal static class ScheduleCalendar
{
    internal static readonly TimeSpan VietnamOffset=TimeSpan.FromHours(7);
    internal static BenchScheduleSettings Validate(BenchScheduleSettings value)
    {
        if(value.Times is null||value.Times.Length>8||(value.Enabled&&value.Times.Length==0)||value.GraceMinutes is <1 or >15)
            throw new ArgumentException("Schedule requires 1–8 daily times when enabled and a 1–15 minute grace window.");
        if(value.Times.Any(t=>t is null||!TimeOnly.TryParseExact(t,"HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out _))
            ||value.Times.Distinct(StringComparer.Ordinal).Count()!=value.Times.Length)
            throw new ArgumentException("Use unique HH:mm times, Vietnam UTC+7.");
        var times=value.Times.OrderBy(t=>t,StringComparer.Ordinal).ToArray();
        // Prevent overlapping approval windows, including midnight.
        var minutes=times.Select(t=>{var x=TimeOnly.ParseExact(t,"HH:mm",CultureInfo.InvariantCulture);return x.Hour*60+x.Minute;}).ToArray();
        if(minutes.Length>1)
            for(int i=0;i<minutes.Length;i++)
                if((minutes[(i+1)%minutes.Length]-minutes[i]+1440)%1440<value.GraceMinutes)
                    throw new ArgumentException("Schedule windows overlap.");
        return new(value.Enabled,times,value.GraceMinutes);
    }
    internal static DateTimeOffset? Latest(BenchScheduleSettings settings,DateTimeOffset utc)
    {
        if(!settings.Enabled||settings.Times.Length==0)return null;
        var local=utc.ToOffset(VietnamOffset);
        DateTimeOffset? latest=null;
        foreach(var day in new[]{local.Date.AddDays(-1),local.Date})
        foreach(var time in settings.Times)
        {
            var due=new DateTimeOffset(day+TimeOnly.ParseExact(time,"HH:mm",CultureInfo.InvariantCulture).ToTimeSpan(),VietnamOffset).ToUniversalTime();
            if(due<=utc&&(!latest.HasValue||due>latest.Value))latest=due;
        }
        return latest;
    }
    internal static DateTimeOffset? Next(BenchScheduleSettings settings,DateTimeOffset utc)
    {
        if(!settings.Enabled||settings.Times.Length==0)return null;
        var local=utc.ToOffset(VietnamOffset);DateTimeOffset? next=null;
        foreach(var day in new[]{local.Date,local.Date.AddDays(1)})
        foreach(var time in settings.Times)
        {
            var due=new DateTimeOffset(day+TimeOnly.ParseExact(time,"HH:mm",CultureInfo.InvariantCulture).ToTimeSpan(),VietnamOffset).ToUniversalTime();
            if(due>utc&&(!next.HasValue||due<next.Value))next=due;
        }
        return next;
    }
}

// A single local writer. Persist slot consumption BEFORE UART transmission.
// No automatic restart/replay; no dependence on SQL Server or Backend.
public sealed class BenchSchedule : IDisposable
{
    private readonly object _gate=new();
    private readonly WaterBenchSession _session;
    private readonly WaterBenchOptions _options;
    private readonly string _file;
    private FileStream? _lease;
    private ScheduleState _state;
    private readonly DateTimeOffset _bootUtc;
    private bool _storageFault;
    private long _lastProbeMs;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    internal BenchSchedule(WaterBenchOptions options,WaterBenchSession session,string directory,DateTimeOffset now)
    {
        _options=options;_session=session;_bootUtc=now;
        _file=Path.Combine(Path.GetFullPath(directory),"schedule.json");
        _state=new(1,new(false,Array.Empty<string>()),now,null,null);
        if(!options.Enabled)return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            _lease=new FileStream(_file+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            if(File.Exists(_file))
            {
                if(new FileInfo(_file).Length>16384)throw new IOException("Schedule file too large.");
                var state=JsonSerializer.Deserialize<ScheduleState>(File.ReadAllBytes(_file),Json)??throw new IOException("Missing schedule state.");
                if(state.Version!=1||state.ChangedUtc==default||state.Settings is null||
                    (state.Slot is not null&&(state.Slot.Id==Guid.Empty||state.LastDueUtc!=state.Slot.DueUtc||
                        state.Slot.ExpiresUtc<=state.Slot.DueUtc||state.Slot.State is not ("due" or "missed" or "expired" or "dispatch_attempt" or "sent_unconfirmed" or "blocked" or "interrupted"))))
                    throw new IOException("Invalid schedule journal.");
                _state=state with {Settings=ScheduleCalendar.Validate(state.Settings)};
                if(_state.Slot?.State is "due" or "dispatch_attempt")
                    Save(_state with {Slot=_state.Slot with {State="interrupted",Reason="restart_no_replay"}});
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { _storageFault=true;_lease?.Dispose();_lease=null; }
    }
    private bool Save(ScheduleState value)
    {
        if(_storageFault||_lease is null)return false;
        string temp=_file+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(stream,value,Json);stream.Flush(flushToDisk:true);}
            File.Move(temp,_file,overwrite:true);
            _state=value;return true;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {_storageFault=true;return false;}
        finally {try{if(File.Exists(temp))File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}}
    }
    public bool Configure(BenchScheduleSettings settings,DateTimeOffset now,out string message)
    {
        lock(_gate)
        {
            message="Schedule storage unavailable.";
            if(!_options.Enabled||_storageFault)return false;
            BenchScheduleSettings valid;
            try{valid=ScheduleCalendar.Validate(settings);}
            catch(ArgumentException ex){message=ex.Message;return false;}
            // A config update cancels any pending approval and never rewinds the consumed-slot watermark.
            var watermark=_state.LastDueUtc is { } previous&&previous>now?previous:now;
            if(!Save(new(1,valid,now,watermark,null)))return false;
            message="Saved; future slots only.";return true;
        }
    }
    internal void Tick(DateTimeOffset utc)
    {
        lock(_gate)
        {
            if(!_options.Enabled||_storageFault||!_state.Settings.Enabled)return;
            if(_state.Slot is {State:"due"} old&&utc>=old.ExpiresUtc)
                if(!Save(_state with {Slot=old with {State="expired",Reason="approval_window_elapsed"}}))return;
            var latest=ScheduleCalendar.Latest(_state.Settings,utc);
            if(latest is { } due&&due>_state.ChangedUtc&&(_state.LastDueUtc is null||due>_state.LastDueUtc.Value))
            {
                var expires=due.AddMinutes(_state.Settings.GraceMinutes);
                bool missed=due<_bootUtc||utc>=expires;
                var slot=new ScheduleSlot(Guid.NewGuid(),due,expires,missed?"missed":"due",missed?"offline_or_late_no_catchup":null);
                if(!Save(_state with {LastDueUtc=due,Slot=slot}))return;
            }
            if(_state.Slot is {State:"due"}&&Environment.TickCount64-_lastProbeMs>=5000)
            {
                _lastProbeMs=Environment.TickCount64;
                _session.ProbeScheduleReadiness();
            }
        }
    }
    public object Snapshot(DateTimeOffset now)
    {
        lock(_gate)return new {
            enabled=_options.Enabled&&!_storageFault&&_state.Settings.Enabled,storageHealthy=!_storageFault&&_lease is not null,
            timezone="UTC+07:00 (Vietnam)",settings=_state.Settings,nextDueUtc=ScheduleCalendar.Next(_state.Settings,now),
            lastSlot=_state.Slot,approvalRequired=true,allAnalytesMask=7,
            readiness=_session.ScheduleReadiness(),autoReplay=false
        };
    }
    public bool Approve(Guid slotId,DateTimeOffset now,out string message)
    {
        lock(_gate)
        {
            message="Slot unavailable.";
            if(_storageFault||!_state.Settings.Enabled||_state.Slot is not {State:"due"} slot||
                slot.Id!=slotId||now<slot.DueUtc||now>=slot.ExpiresUtc)return false;
            var reason=_session.ScheduleReadiness();
            if(reason!="ready"){message=reason;return false;}
            // Even a crash between this durable write and UART send consumes the occurrence.
            if(!Save(_state with {Slot=slot with {State="dispatch_attempt"}})){message="Schedule journal failed.";return false;}
            bool sent=_session.TryCommand(new BenchCommand(slot.Id,"scheduled",Mask:7),out message);
            bool recorded=Save(_state with {Slot=slot with {State=sent?"sent_unconfirmed":"blocked",Reason=sent?null:message}});
            if(!recorded)message="Dispatch outcome journal failed; inspect hardware, no replay.";
            // A sent command remains sent even if the post-send journal fails.
            return sent;
        }
    }
    public void Dispose(){lock(_gate){_lease?.Dispose();_lease=null;}}
}

public sealed class BenchScheduleWorker : BackgroundService
{
    private readonly BenchSchedule _schedule;
    public BenchScheduleWorker(BenchSchedule schedule)=>_schedule=schedule;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do{_schedule.Tick(DateTimeOffset.UtcNow);}
            while(await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){}
    }
}
