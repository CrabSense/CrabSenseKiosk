using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.Options;

namespace CrabSenseKiosk.Services.WaterBench;

public sealed class WaterBenchWorker : BackgroundService
{
    private readonly WaterBenchOptions _options;
    private readonly WaterBenchSession _session;
    private readonly ILogger<WaterBenchWorker> _log;
    public WaterBenchWorker(IOptions<WaterBenchOptions> options,WaterBenchSession session,ILogger<WaterBenchWorker> log)
        =>(_options,_session,_log)=(options.Value,session,log);
    private static SerialPort Port(string name)=>new(name,115200,Parity.None,8,StopBits.One)
    { ReadTimeout=250,WriteTimeout=500,DtrEnable=false,RtsEnable=false,Handshake=Handshake.None,Encoding=Encoding.ASCII };
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!_options.Enabled)return;
        if(string.IsNullOrWhiteSpace(_options.MasterPort)||string.IsNullOrWhiteSpace(_options.SlavePort)||
            string.Equals(_options.MasterPort,_options.SlavePort,StringComparison.OrdinalIgnoreCase)||
            !WireProtocol.HardwareId(_options.MasterHardwareId)||!WireProtocol.HardwareId(_options.SlaveHardwareId)||
            _options.MasterHardwareId==_options.SlaveHardwareId)
        { _log.LogError("Water bench requires two explicitly configured ports and distinct hardware IDs.");return; }
        // Do not auto-reopen a board after an uncertain disconnect: USB UART open may reset it.
        try
        {
            using var master=Port(_options.MasterPort);using var slave=Port(_options.SlavePort);
            master.Open();slave.Open();
            await Task.Delay(TimeSpan.FromSeconds(3),ct);
            _session.Connected(line=>master.Write(line+"\n"));
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);
            var readMaster=Task.Run(()=>Read(master,true,linked.Token),linked.Token);
            var readSlave=Task.Run(()=>Read(slave,false,linked.Token),linked.Token);
            var maintenance=MaintainAsync(linked.Token);
            try
            {
                // Only read-only identification probes. No automatic START or replay.
                master.Write("inventory\n");slave.Write("inventory\n");
                await Task.WhenAny(readMaster,readSlave,maintenance);
            }
            finally {linked.Cancel();try{await Task.WhenAll(readMaster,readSlave,maintenance);}catch(OperationCanceledException) when(linked.IsCancellationRequested){}}
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){}
        catch(Exception ex){_log.LogError("Water bench serial session ended: {Type}",ex.GetType().Name);}
        finally{_session.Disconnected();}
    }
    private async Task MaintainAsync(CancellationToken ct)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while(await timer.WaitForNextTickAsync(ct))_session.Maintain();
    }
    private void Read(SerialPort port,bool master,CancellationToken ct)
    {
        var line=new StringBuilder();bool invalid=false;
        while(!ct.IsCancellationRequested)
        {
            int b;
            try{b=port.ReadByte();}catch(TimeoutException){continue;}
            if(b<0)throw new IOException("Serial EOF.");
            if(b==10||b==13)
            {
                if(invalid)throw new IOException("Malformed or oversized serial line.");
                if(line.Length>0){_session.Feed(master,line.ToString());line.Clear();}
            }
            else if(b<32||b>126||line.Length>=WireProtocol.MaxLine)invalid=true;
            else if(!invalid)line.Append((char)b);
        }
    }
}
