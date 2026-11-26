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
    private BatchObservationJournal? _timeline;
    private bool _fault;
    private Guid _connection;
    private string? _error;
    private long _lastTelemetryStored,_batchAtMs,_cleanAtMs,_telemetryAtMs;
    private bool _startPending;
    private Guid _helloNonce;
    private long _helloSentMs;
    private DateTimeOffset _helloSentUtc;
    private string? _boot;
    private uint _lastSequence;
    private ClockObservation? _clock;
    private PendingCommand? _pending;
    private sealed record PendingCommand(Guid Id,uint Seq,string Action,long SentMs);
    private sealed record ImageLink(string Batch,string Analyte,string Session,uint CaptureId,DateTimeOffset At,long MonotonicMs);
    private static long NowMs => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));

    internal WaterBenchSession(WaterBenchOptions options,EvidenceSpool spool)
        => (_options,_spool)=(options,spool);

    internal void Connected(Action<string> write)
    {
        lock(_gate)
        {
            _connection=Guid.NewGuid(); _write=write; _timeline=null;
            _master=_slave=_batch=_clean=_sample=null; _telemetry=null; _telemetryAt=null;
            _activeBatch=null; _startPending=false; _boot=null; _clock=null; _helloNonce=Guid.Empty; _pending=null; _lastSequence=0; _batchAtMs=_cleanAtMs=_telemetryAtMs=0; _sessions.Clear(); _links.Clear(); _frames.Clear(); _saved.Clear(); _camera.Reset();
            // Reconnect never silently releases prior uncertain state.
            Store("connection_open",new { connection=_connection });
        }
    }
    internal void Disconnected()
    {
        lock(_gate)
        {
            if (_activeBatch is not null || _write is not null) { _fault=true; _error="connection_lost_inspect_before_restart"; }
            InterruptTimeline("disconnect");
            EndUnknown("disconnect");
            _write=null; _master=_slave=null; _boot=null; _clock=null; _camera.Reset(); _links.Clear(); _frames.Clear(); _saved.Clear();
            try { Store("connection_closed",new { reason=_error }); } catch(IOException) { }
        }
    }
    private void Store(string kind,object value) => _spool.Event(new {
        kind,connection=_connection,receivedAt=DateTimeOffset.UtcNow,value,uploadState="pending"
    });
    private bool Identified => _master.HasValue && _slave.HasValue && _write is not null;
    private void EndUnknown(string reason)
    {
        if(_pending is not { } p)return;
        _pending=null;
        try{_spool.Outcome(p.Id,new {state="unknown",reason,connection=_connection,bootId=_boot,sequence=p.Seq,at=DateTimeOffset.UtcNow});}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
    }
    private void InterruptTimeline(string reason)
    {
        try{_timeline?.Interrupt(reason,DateTimeOffset.UtcNow);}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){_fault=true;_error="batch_timeline_storage_failed";}
    }
    private void Fault(string error)
    {
        // Firmware fault line is followed by a structured terminal status.
        if(error!="firmware_reported_fault"&&error!="firmware_batch_fault")InterruptTimeline(error);
        EndUnknown(error);_fault=true;_error=error;_camera.Reset();_frames.Clear();
    }
    private void SendHelloLocked()
    {
        if(!Identified||_fault||_pending is not null||_helloNonce!=Guid.Empty)return;
        _helloNonce=Guid.NewGuid();_helloSentMs=NowMs;_helloSentUtc=DateTimeOffset.UtcNow;
        _write!("gw hello "+_helloNonce.ToString("N"));
    }
    internal void Maintain()
    {
        lock(_gate)
        {
            if(_pending is { } p&&NowMs-p.SentMs>=10000)Fault("ack_timeout_no_replay");
            if(_helloNonce!=Guid.Empty&&NowMs-_helloSentMs>=3000){_helloNonce=Guid.Empty;Fault("gateway_handshake_timeout");}
            if(!Identified||_fault)return;
            try
            {
                if(_boot is null||(_clock is not null&&NowMs-_clock.ObservedMonotonicMs>=60000))SendHelloLocked();
            }
            catch(Exception ex) when(ex is IOException or InvalidOperationException or TimeoutException){Fault("gateway_handshake_transport");}
        }
    }
    public object? CommandStatus(Guid id){lock(_gate)return _spool.CommandStatus(id);}
    private object? SampleClock(JsonElement e)
    {
        if(_clock is not { } c||_boot!=c.Boot)return null;
        return new {bootId=c.Boot,estimatedSampleUtc=c.Estimate(e.GetProperty("sample_uptime_ms").GetUInt32(),NowMs),
            estimatedPhObservationUtc=c.Estimate(e.GetProperty("ph_observed_uptime_ms").GetUInt32(),NowMs),
            estimatedTemperatureObservationUtc=c.Estimate(e.GetProperty("temperature_observed_uptime_ms").GetUInt32(),NowMs),
            anchorRoundTripMs=c.RoundTripMs,clockDriftValidated=false,physicalAlignmentVerified=false,
            basis="estimated_master_uptime_not_actual_exposure"};
    }
    internal void Feed(bool master,string line)
    {
        lock(_gate)
        {
            try { Handle(master,line); }
            catch(Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or KeyNotFoundException or OverflowException or IOException or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException or UnauthorizedAccessException)
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
        if(master&&line.StartsWith("GW HELLO ",StringComparison.Ordinal))
        {
            if(_helloNonce==Guid.Empty)throw new FormatException("Unsolicited gateway handshake.");
            var h=GatewayWire.Hello(WireProtocol.Json(line[9..]),_helloNonce);
            long rtt=now-_helloSentMs;
            _helloNonce=Guid.Empty;
            if(rtt<0||rtt>=3000||Math.Abs((utc-_helloSentUtc).TotalMilliseconds-rtt)>500||
                (_boot is not null&&(_boot!=h.Boot||h.LastSequence!=_lastSequence)))
            {Fault("gateway_boot_sequence_or_clock_changed");return;}
            _boot=h.Boot;_lastSequence=h.LastSequence;
            _clock=new(h.Boot,h.UptimeMs,_helloSentUtc.AddMilliseconds(rtt/2.0),rtt,now);
            Store("clock_observation",_clock);return;
        }
        if(master&&line.StartsWith("GW ACK ",StringComparison.Ordinal))
        {
            var ack=GatewayWire.Ack(WireProtocol.Json(line[7..]));
            if(_pending is not { } p||ack.RequestId!=p.Id||ack.Sequence!=p.Seq||ack.Boot!=_boot)
            {Fault("unmatched_gateway_ack");return;}
            if(ack.Reason is "boot_mismatch" or "sequence_mismatch"){Fault("gateway_sequence_conflict");return;}
            bool start=p.Action is "manual" or "scheduled";
            if(start&&ack.Accepted&&(_activeBatch is null||ack.BatchId!=_activeBatch))
            {Fault("batch_start_ack_mismatch");return;}
            _spool.Outcome(p.Id,new {state=ack.Accepted?"accepted":"rejected",ack,connection=_connection,receivedAt=utc,
                estimatedControllerUtc=_clock?.Estimate(ack.UptimeMs,now),physicalCompletionConfirmed=false});
            _pending=null;_lastSequence=ack.Sequence;
            if(start){_startPending=false;_batchAtMs=_cleanAtMs=0;}
            return;
        }
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
            if(_options.BackendStationId!=Guid.Empty)
            {
                if(_boot is null)throw new FormatException("Batch start without gateway boot identity.");
                _timeline=new BatchObservationJournal(_options.BackendStationId,_connection,_boot,_spool);
                _timeline.Start(line,_pending is {Action:"manual" or "scheduled"} pending?pending.Id:null,utc);
            }
            _activeBatch=p[2];_startPending=false;_sessions.Clear();_saved.Clear();_links.Clear();_frames.Clear();_camera.Reset();
            Store("batch_start",line);return;
        }
        if(line.StartsWith("BATCH SESSION ",StringComparison.Ordinal))
        {
            var e=WireProtocol.Json(line[14..]);var b=e.GetProperty("batch_id").GetString();
            var a=e.GetProperty("analyte").GetString();var session=e.GetProperty("session_id").GetString();
            if(b!=_activeBatch||b is null||a is not ("NO2" or "NO3" or "NH3")||session is null||
                (a=="NH3"?!WireProtocol.BatchId(session):session!=b+(a=="NO2"?"-1":"-2"))||!_sessions.TryAdd(a,session))
                throw new FormatException("Session mismatch.");
            _timeline?.Session(e,utc);Store("session",e);return;
        }
        if(line.StartsWith("BATCH REACTION ",StringComparison.Ordinal))
        {
            _timeline?.Reaction(line,utc);Store("operator_event",line);return;
        }
        if(line.StartsWith("BATCH IMAGE ",StringComparison.Ordinal))
        {
            var e=WireProtocol.Json(line[12..]);var b=e.GetProperty("batch_id").GetString();
            var a=e.GetProperty("analyte").GetString();var sid=e.GetProperty("session_id").GetString();
            uint id=e.GetProperty("capture_id").GetUInt32();
            if(b!=_activeBatch||b is null||a is null||sid is null||!_sessions.TryGetValue(a,out var expected)||sid!=expected||
                id is 0 or >65536||_saved.ContainsKey(id)||_links.Count>=3||!_links.TryAdd(id,new(b,a,sid,id,utc,now)))
                throw new FormatException("Image request mismatch.");
            _timeline?.Image(e,utc);Store("image_request",e);Pair(id);return;
        }
        if(line.StartsWith("BATCH STATUS ",StringComparison.Ordinal))
        {
            var e=WireProtocol.Json(line[13..]);int state=e.GetProperty("state").GetInt32();
            if(state<0||state>3)throw new FormatException("Batch state.");
            if(state==1&&e.GetProperty("batch_id").GetString()!=_activeBatch)Fault("unknown_running_batch");
            _timeline?.Status(e,utc);_batch=e;_batchAtMs=now;Store("batch_status",e);
            if(state==3)Fault("firmware_batch_fault");
            return;
        }
        if(line.StartsWith("CLEAN STATUS ",StringComparison.Ordinal))
        {
            _clean=WireProtocol.Json(line[13..]);_cleanAtMs=now;Store("clean_status",_clean.Value);return;
        }
        if(line.StartsWith("NH3 SAMPLE ",StringComparison.Ordinal))
        {
            var e=WireProtocol.Json(line[11..]);
            if(_sessions.TryGetValue("NH3",out var sid)&&e.GetProperty("session_id").GetString()==sid)
            {_sample=e;Store("sample_evidence",new {sample=e,clock=SampleClock(e)});}
            else Store("unassociated_sample_evidence",e);
            return;
        }
        if(line.StartsWith("NH3 IMAGE_LINK ",StringComparison.Ordinal))
        {Store("nh3_image_link",WireProtocol.Json(line[15..]));return;}
        if(line.StartsWith("link=",StringComparison.Ordinal))
        {
            _telemetry=line.Split(' ',StringSplitOptions.RemoveEmptyEntries).Select(p=>p.Split('=',2))
                .Where(p=>p.Length==2).ToDictionary(p=>p[0],p=>p[1],StringComparer.Ordinal);
            _telemetryAt=utc;_telemetryAtMs=now;
            if(now-_lastTelemetryStored>=10000){Store("telemetry_observation",_telemetry);_lastTelemetryStored=now;}
            return;
        }
        if(line.StartsWith("BATCH RESET",StringComparison.Ordinal))
        {
            InterruptTimeline("reset_before_terminal_status");_timeline=null;
            _activeBatch=null;_sessions.Clear();_links.Clear();_frames.Clear();_saved.Clear();_camera.Reset();Store("operator_event",line);return;
        }
        // Only a matching structured ACK may resolve a command. Human-readable REJECT is evidence only.
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
            activeBatch=_activeBatch,startPending=_startPending,gatewayBootId=_boot,gatewayReady=_boot is not null&&!_fault,pendingCommandId=_pending?.Id,clockObservation=_clock,scheduleReadiness=ReadinessLocked(),savedCaptureIds=_saved.Keys.ToArray(),clockBasis="kiosk_receipt_not_exposure",
            physicalAlignmentVerified=false,backendUploadImplemented=false
        };
    }
    private string ReadinessLocked()
    {
        if(!_options.Enabled||!_options.EnableSupervisedControl)return "supervised_control_disabled";
        if(!Identified||_fault||_boot is null)return "connection_or_recovery_required";
        if(_pending is not null||_helloNonce!=Guid.Empty)return "gateway_busy";
        if(_startPending||_activeBatch is not null)return "batch_already_held_or_start_pending";
        if(!_spool.HasCapacity)return "spool_full";
        var now=NowMs;
        if(_batch is null||_clean is null||_telemetry is null||now-_batchAtMs>15000||now-_cleanAtMs>15000||now-_telemetryAtMs>15000)
            return "fresh_status_required";
        try
        {
            if(!_master!.Value.GetProperty("outputsEnabled").GetBoolean()||!_slave!.Value.GetProperty("outputsEnabled").GetBoolean()||
                !_master.Value.GetProperty("rs485Enabled").GetBoolean()||!_slave.Value.GetProperty("rs485Enabled").GetBoolean()||
                !_slave.Value.GetProperty("cameraSupported").GetBoolean())return "firmware_capability_disabled";
            if(_batch.Value.GetProperty("state").GetInt32()!=0)return "batch_not_idle";
            if(!_clean.Value.GetProperty("ready_for_assay").GetBoolean()||_clean.Value.GetProperty("dirty_mask").GetInt32()!=0||
                !_clean.Value.GetProperty("storage_ok").GetBoolean())return "cleaning_required";
            foreach(var pair in new[]{("link","1"),("local","0"),("armed","0"),("slave_state","0"),("slave_ch","0"),("cal_mask","0x7f"),("storage_ok","1"),("dirty_mask","0")})
                if(!_telemetry.TryGetValue(pair.Item1,out var value)||value!=pair.Item2)return "controller_not_ready";
            return "ready";
        }
        catch(Exception ex) when(ex is InvalidOperationException or KeyNotFoundException or FormatException){return "invalid_readiness_status";}
    }
    internal string ScheduleReadiness(){lock(_gate)return ReadinessLocked();}
    internal void ProbeScheduleReadiness()
    {
        lock(_gate)
        {
            if(!Identified||_fault||_pending is not null||_helloNonce!=Guid.Empty||_startPending||_activeBatch is not null||!_spool.HasCapacity)return;
            try{_write!("batch status");_write("clean status");}
            catch(Exception ex) when(ex is IOException or InvalidOperationException or TimeoutException){Fault("readiness_probe_failed");}
        }
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
                catch(TimeoutException){Fault("stop_transport_failed");return false;}
            }
            if(!Identified||_fault||_boot is null||!_spool.HasCapacity)return false;
            if(_pending is not null||_helloNonce!=Guid.Empty){message="Gateway busy; no duplicate dispatch.";return false;}
            if(_spool.HasCommand(request.RequestId)){message="Request ID already recorded; query command status.";return false;}
            if(_startPending){message="Start pending; inspect firmware, no retry.";return false;}
            bool starting=request.Action is "manual" or "scheduled";
            if(starting&&_activeBatch is not null){message="Batch held.";return false;}
            if(request.Action=="scheduled"&&ReadinessLocked()!="ready"){message=ReadinessLocked();return false;}
            string? command=BuildCommand(request);
            if(command is null)return false;
            if(request.Action=="image" && (!_saved.TryGetValue(request.CaptureId,out var saved)||_activeBatch is null||saved.Analyte!=(request.Id==1?"NO2":request.Id==2?"NO3":"NH3")))return false;
            try
            {
                // Journal before serial transmission; repeated request IDs never resend.
                // Status probes do not mutate firmware and need no sequence.
                if(request.Action is "batchStatus" or "cleanStatus")
                {_write(command);message="Status probe sent";return true;}
                if(_lastSequence==uint.MaxValue){Fault("sequence_exhausted");return false;}
                uint seq=_lastSequence+1;
                string frame=GatewayWire.Command(_boot,seq,request.RequestId,command);
                _spool.Command(request.RequestId,new {kind="command",request,command,connection=_connection,bootId=_boot,sequence=seq,at=DateTimeOffset.UtcNow,state="send_attempt"});
                _pending=new(request.RequestId,seq,request.Action,NowMs);
                if(starting){_startPending=true;_batchAtMs=_cleanAtMs=0;}
                _write(frame);message="Sent to UART; query command status for ACK";return true;
            }
            catch(IOException){Fault("command_journal_or_transport_failed");return false;}
            catch(UnauthorizedAccessException){Fault("command_journal_access_failed");return false;}
            catch(InvalidOperationException){Fault("command_transport_failed");return false;}
            catch(TimeoutException){Fault("command_transport_failed");return false;}
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
