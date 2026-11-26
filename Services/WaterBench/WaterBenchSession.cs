using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrabSenseKiosk.Services.WaterBench;

public sealed record BenchCommand(Guid RequestId,string Action,int Id=0,int Mask=0,uint CaptureId=0,uint[]? Values=null);

public sealed class WaterBenchSession
{
    private readonly object _gate = new();
    private readonly WaterBenchOptions _options;
    private readonly EvidenceSpool _spool;
    private readonly CameraReceiver _camera = new();
    private readonly Dictionary<uint,ImageLink> _links = new();
    private readonly Dictionary<uint,ReceivedFrame> _frames = new();
    private readonly Dictionary<uint,(string MediaId,string Analyte)> _saved = new();
    private readonly Dictionary<string,string> _sessions = new();
    private Action<string>? _write;
    private JsonElement? _master,_slave,_batch,_clean,_sample;
    private Dictionary<string,string>? _telemetry;
    private DateTimeOffset? _telemetryAt;
    private string? _activeBatch;
    private bool _fault;
    private Guid _connection;
    private string? _error;
    private long _lastTelemetryStored;
    private sealed record ImageLink(string Batch,string Analyte,string Session,uint CaptureId,DateTimeOffset At,long MonotonicMs);
    private static long NowMs => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));

    internal WaterBenchSession(WaterBenchOptions options,EvidenceSpool spool)
        => (_options,_spool)=(options,spool);

    internal void Connected(Action<string> write)
    {
        lock(_gate)
        {
            _connection=Guid.NewGuid(); _write=write;
            _master=_slave=_batch=_clean=_sample=null; _telemetry=null; _telemetryAt=null;
            _activeBatch=null; _sessions.Clear(); _links.Clear(); _frames.Clear(); _saved.Clear(); _camera.Reset();
            // Reconnect never silently releases prior uncertain state.
            Store("connection_open",new { connection=_connection });
        }
    }
    internal void Disconnected()
    {
        lock(_gate)
        {
            if (_activeBatch is not null || _write is not null) { _fault=true; _error="connection_lost_inspect_before_restart"; }
            _write=null; _master=_slave=null; _camera.Reset(); _links.Clear(); _frames.Clear(); _saved.Clear();
            try { Store("connection_closed",new { reason=_error }); } catch(IOException) { }
        }
    }
    private void Store(string kind,object value) => _spool.Event(new {
        kind,connection=_connection,receivedAt=DateTimeOffset.UtcNow,value,uploadState="pending"
    });
    private bool Identified => _master.HasValue && _slave.HasValue && _write is not null;
    private void Fault(string error) { _fault=true;_error=error;_camera.Reset();_frames.Clear(); }
    internal void Feed(bool master,string line)
    {
        lock(_gate)
        {
            try { Handle(master,line); }
            catch(Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or KeyNotFoundException or OverflowException or IOException or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException)
            { Fault("invalid_or_unstored_wire_evidence"); }
        }
    }
    private void Handle(bool master,string line)
    {
        if (line.Length>WireProtocol.MaxLine) throw new FormatException("Oversize line.");
        if (line.StartsWith("INVENTORY ",StringComparison.Ordinal))
        {
            var e=WireProtocol.Json(line[10..]);
            if (!WireProtocol.Inventory(e,master,master?_options.MasterHardwareId:_options.SlaveHardwareId))
            { Fault("inventory_identity_mismatch"); if(master)_master=null;else _slave=null; return; }
            if (master?_master.HasValue:_slave.HasValue)
            {
                // A fresh boot/inventory while work is held is ambiguous.
                if(_activeBatch is not null) Fault("inventory_during_batch");
            }
            if(master)_master=e;else _slave=e;
            Store("inventory",e);return;
        }
        if (!Identified) return;
        var utc=DateTimeOffset.UtcNow;var now=NowMs;
        foreach(var id in _links.Where(x=>now-x.Value.MonotonicMs>60000).Select(x=>x.Key).ToArray()) _links.Remove(id);
        foreach(var id in _frames.Where(x=>utc-x.Value.ReceivedAt>TimeSpan.FromSeconds(60)).Select(x=>x.Key).ToArray()) _frames.Remove(id);
        if (!master)
        {
            if(line.StartsWith("CAM ERROR ",StringComparison.Ordinal))
            { Fault("camera_error");Store("camera_error",line);return; }
            if(line.StartsWith("CAM ",StringComparison.Ordinal))
            {
                var frame=_camera.Feed(line,utc,now);
                if(frame is not null)
                {
                    if(_activeBatch is null||_frames.Count>=3||_saved.ContainsKey(frame.CaptureId)||!_frames.TryAdd(frame.CaptureId,frame))
                        throw new FormatException("Unexpected or duplicate frame.");
                    Pair(frame.CaptureId);
                }
            }
            return;
        }
        if(line.StartsWith("BATCH START ",StringComparison.Ordinal))
        {
            var p=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
            if(p.Length!=5||!WireProtocol.BatchId(p[2])||_activeBatch is not null)throw new FormatException("Unexpected batch start.");
            _activeBatch=p[2];_sessions.Clear();_saved.Clear();_links.Clear();_frames.Clear();_camera.Reset();
            Store("batch_start",line);return;
        }
        if(line.StartsWith("BATCH SESSION ",StringComparison.Ordinal))
        {
            var e=WireProtocol.Json(line[14..]);var b=e.GetProperty("batch_id").GetString();
            var a=e.GetProperty("analyte").GetString();var session=e.GetProperty("session_id").GetString();
            if(b!=_activeBatch||b is null||a is not ("NO2" or "NO3" or "NH3")||session is null||
                (a=="NH3"?!WireProtocol.BatchId(session):session!=b+(a=="NO2"?"-1":"-2"))||!_sessions.TryAdd(a,session))
                throw new FormatException("Session mismatch.");
            Store("session",e);return;
        }
        if(line.StartsWith("BATCH IMAGE ",StringComparison.Ordinal))
        {
            var e=WireProtocol.Json(line[12..]);var b=e.GetProperty("batch_id").GetString();
            var a=e.GetProperty("analyte").GetString();var sid=e.GetProperty("session_id").GetString();
            uint id=e.GetProperty("capture_id").GetUInt32();
            if(b!=_activeBatch||b is null||a is null||sid is null||!_sessions.TryGetValue(a,out var expected)||sid!=expected||
                id is 0 or >65536||_saved.ContainsKey(id)||_links.Count>=3||!_links.TryAdd(id,new(b,a,sid,id,utc,now)))
                throw new FormatException("Image request mismatch.");
            Store("image_request",e);Pair(id);return;
        }
        if(line.StartsWith("BATCH STATUS ",StringComparison.Ordinal))
        {
            var e=WireProtocol.Json(line[13..]);int state=e.GetProperty("state").GetInt32();
            if(state<0||state>3)throw new FormatException("Batch state.");
            if(state==1&&e.GetProperty("batch_id").GetString()!=_activeBatch)Fault("unknown_running_batch");
            _batch=e;Store("batch_status",e);
            if(state==3)Fault("firmware_batch_fault");
            return;
        }
        if(line.StartsWith("CLEAN STATUS ",StringComparison.Ordinal))
        {
            _clean=WireProtocol.Json(line[13..]);Store("clean_status",_clean.Value);return;
        }
        if(line.StartsWith("NH3 SAMPLE ",StringComparison.Ordinal))
        {
            var e=WireProtocol.Json(line[11..]);
            if(_sessions.TryGetValue("NH3",out var sid)&&e.GetProperty("session_id").GetString()==sid)
            {_sample=e;Store("sample_evidence",e);}
            else Store("unassociated_sample_evidence",e);
            return;
        }
        if(line.StartsWith("NH3 IMAGE_LINK ",StringComparison.Ordinal))
        {Store("nh3_image_link",WireProtocol.Json(line[15..]));return;}
        if(line.StartsWith("link=",StringComparison.Ordinal))
        {
            _telemetry=line.Split(' ',StringSplitOptions.RemoveEmptyEntries).Select(p=>p.Split('=',2))
                .Where(p=>p.Length==2).ToDictionary(p=>p[0],p=>p[1],StringComparer.Ordinal);
            _telemetryAt=utc;
            if(now-_lastTelemetryStored>=10000){Store("telemetry_observation",_telemetry);_lastTelemetryStored=now;}
            return;
        }
        if(line.StartsWith("BATCH RESET",StringComparison.Ordinal))
        {
            _activeBatch=null;_sessions.Clear();_links.Clear();_frames.Clear();_saved.Clear();_camera.Reset();Store("operator_event",line);return;
        }
        if(line.StartsWith("BATCH FAULT",StringComparison.Ordinal)||line.StartsWith("CLEAN FAULT",StringComparison.Ordinal))
            Fault("firmware_reported_fault");
        if(line.StartsWith("BATCH ",StringComparison.Ordinal)||line.StartsWith("CLEAN ",StringComparison.Ordinal)||line.StartsWith("REJECT",StringComparison.Ordinal))
            Store("operator_event",line);
    }
    private void Pair(uint id)
    {
        if(_fault||!_links.TryGetValue(id,out var link)||!_frames.TryGetValue(id,out var frame))return;
        if(Math.Abs((frame.ReceivedAt-link.At).TotalSeconds)>60)throw new FormatException("Capture outside request window.");
        string mediaId=_spool.Frame(frame,link);
        _saved.Add(id,(mediaId,link.Analyte));_links.Remove(id);_frames.Remove(id);
    }
    public object Snapshot()
    {
        lock(_gate)return new {
            enabled=_options.Enabled,controlEnabled=_options.EnableSupervisedControl,connection=_connection,
            identified=Identified,recoveryRequired=_fault,error=_error,spoolCapacity=_spool.HasCapacity,
            master=_master,slave=_slave,batch=_batch,clean=_clean,nh3Sample=_sample,
            telemetry=_telemetry is null?null:new Dictionary<string,string>(_telemetry),telemetryReceivedAt=_telemetryAt,
            activeBatch=_activeBatch,savedCaptureIds=_saved.Keys.ToArray(),clockBasis="kiosk_receipt_not_exposure",
            physicalAlignmentVerified=false,backendUploadImplemented=false
        };
    }
    public bool TryCommand(BenchCommand request,out string message)
    {
        lock(_gate)
        {
            message="Rejected";
            if(!_options.EnableSupervisedControl||request.RequestId==Guid.Empty||_write is null)return false;
            // STOP can be sent even if the slave or evidence spool is unavailable.
            if(request.Action=="stop")
            {
                try{_write("stop");message="STOP sent; physical stop not confirmed";return true;}
                catch(IOException){Fault("stop_transport_failed");return false;}
                catch(InvalidOperationException){Fault("stop_transport_failed");return false;}
            }
            if(!Identified||_fault||!_spool.HasCapacity)return false;
            string? command=BuildCommand(request);
            if(command is null)return false;
            if(request.Action=="image" && (!_saved.TryGetValue(request.CaptureId,out var saved)||_activeBatch is null||saved.Analyte!=(request.Id==1?"NO2":request.Id==2?"NO3":"NH3")))return false;
            try
            {
                // Journal before serial transmission; repeated request IDs never resend.
                _spool.Command(request.RequestId,new { kind="command",request,command,connection=_connection,at=DateTimeOffset.UtcNow,state="send_attempt" });
                _write(command);message="Sent to UART; firmware acceptance not confirmed";return true;
            }
            catch(IOException){Fault("command_journal_or_transport_failed");return false;}
            catch(InvalidOperationException){Fault("command_transport_failed");return false;}
        }
    }
    internal static string? BuildCommand(BenchCommand r)
    {
        static bool Pulse(uint x)=>x is >=1 and <=10000;
        static bool Volume(uint x)=>x is >=1 and <=65535;
        static string Numbers(uint[] v)=>string.Join(" ",v.Select(x=>x.ToString(CultureInfo.InvariantCulture)));
        var v=r.Values??Array.Empty<uint>();bool id=r.Id is >=1 and <=3,mask=r.Mask is >=1 and <=7;
        if(r.Action=="assayProfile"&&id&&v.Length==(r.Id==2?7:6)&&Pulse(v[0])&&Pulse(v[1])&&
            v.Skip(2).Take(v.Length-4).All(Volume)&&Pulse(v[^2])&&Pulse(v[^1]))
            return (r.Id==1?"no2":r.Id==2?"no3":"nh3")+" profile "+Numbers(v);
        if(r.Action=="cleanProfile"&&id&&v.Length==6&&Pulse(v[0])&&Pulse(v[1])&&v[2]<=60000&&Pulse(v[3])&&Pulse(v[4])&&v[5] is >=1 and <=5)
            return $"clean profile {r.Id} "+Numbers(v);
        if(v.Length!=0)return null;
        return r.Action switch {
            "manual" when mask=>$"batch start manual {r.Mask}",
            "scheduled" when r.Mask==7=>"batch start scheduled",
            "route" when id=>$"batch route {r.Id}",
            "confirm" when id=>$"batch confirm {r.Id}",
            "optical" when id=>$"batch optical {r.Id}",
            "image" when id&&r.CaptureId is >=1 and <=65536=>$"batch image {r.Id} {r.CaptureId}",
            "batchReset"=>"batch reset",
            "cleanStart" when mask=>$"clean start {r.Mask}",
            "cleanRoute" when id=>$"clean route {r.Id}",
            "cleanVerify" when id=>$"clean verify {r.Id}",
            "cleanReset"=>"clean reset",
            "batchStatus"=>"batch status",
            "cleanStatus"=>"clean status",
            _=>null
        };
    }
}
