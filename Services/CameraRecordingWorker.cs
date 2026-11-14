using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CrabSenseKiosk.Services;

public sealed class CameraRecordingWorker(IConfiguration config, IHttpClientFactory factory,
    ILogger<CameraRecordingWorker> logger) : BackgroundService
{
    private sealed record Envelope<T>(bool Success,T? Data);
    private sealed record Effective(Guid? ScheduleId,int ScheduleVersion,bool ShouldRecord,string? StreamUrl,int? Fps,string? VideoQuality);
    private sealed record Lease(bool ShouldContinue);
    private sealed record Camera(Guid Id,string Key,string? Source,bool Synthetic);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(!config.GetValue<bool>("CameraRecording:Enabled")) return;
        var url=config["CameraRecording:BackendUrl"];
        if(!Uri.TryCreate(url,UriKind.Absolute,out var backend) ||
            (backend.Scheme!="https" && !(backend.Scheme=="http" && backend.IsLoopback)))
        { logger.LogWarning("Recording worker requires HTTPS backend (HTTP loopback allowed for tests).");return; }
        var cameras=config.GetSection("CameraRecording:Cameras").GetChildren().Select(c=>
            new Camera(Guid.TryParse(c["DeviceId"],out var id)?id:Guid.Empty,c["AgentKey"]??"",c["Source"],c.GetValue<bool>("Synthetic")))
            .Where(c=>c.Id!=Guid.Empty&&c.Key.Length>=32).GroupBy(c=>c.Id).Select(g=>g.First()).ToArray();
        await Task.WhenAll(cameras.Select(c=>Loop(backend,c,stoppingToken)));
    }
    private async Task Loop(Uri backend,Camera camera,CancellationToken ct)
    {
        if(camera.Synthetic&&!backend.IsLoopback)
        {logger.LogWarning("Synthetic camera source is restricted to a loopback backend.");return;}
        using var client=factory.CreateClient("camera-recording");
        client.BaseAddress=new Uri(backend.ToString().TrimEnd('/')+"/");
        client.Timeout=TimeSpan.FromSeconds(45);
        client.DefaultRequestHeaders.Add("X-Camera-Agent-Key",camera.Key);
        var root=$"api/cameras/{camera.Id:D}/recording-runtime/";
        while(!ct.IsCancellationRequested)
        {
            try
            {
                var effective=await Get<Effective>(client,root+"configuration",ct);
                if(effective.ShouldRecord&&effective.ScheduleId.HasValue)
                    await Record(client,root,camera,effective,ct);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogWarning("Camera {CameraId} recording attempt failed ({ErrorType}).",camera.Id,ex.GetType().Name);}
            await Task.Delay(TimeSpan.FromSeconds(5),ct);
        }
    }
    private static async Task<T> Get<T>(HttpClient c,string path,CancellationToken ct)
    {
        using var response=await c.GetAsync(path,ct);response.EnsureSuccessStatusCode();
        var body=await response.Content.ReadFromJsonAsync<Envelope<T>>(cancellationToken:ct);
        return body is {Success:true,Data:not null}?body.Data:throw new InvalidOperationException("Invalid camera API response.");
    }
    private static async Task Send(HttpClient c,HttpMethod method,string path,object value,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(method,path){Content=JsonContent.Create(value)};
        using var response=await c.SendAsync(request,ct);response.EnsureSuccessStatusCode();
    }
    private async Task Record(HttpClient client,string root,Camera camera,Effective effective,CancellationToken ct)
    {
        var source=camera.Source??effective.StreamUrl;
        if(!camera.Synthetic&&(!Uri.TryCreate(source,UriKind.Absolute,out var uri)||
            uri.Scheme is not ("rtsp" or "rtsps" or "http" or "https")))
            throw new InvalidOperationException("Camera source not configured.");
        var id=Guid.NewGuid();
        var folder=Path.Combine(Path.GetTempPath(),"crabsense-recording");Directory.CreateDirectory(folder);
        var file=Path.Combine(folder,id.ToString("N")+".mp4");
        var path=root+"executions/"+id.ToString("D");
        var started=false;
        using var process=new Process{StartInfo=new ProcessStartInfo(config["CameraRecording:FfmpegPath"]??"ffmpeg")
            {UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true}};
        var args=process.StartInfo.ArgumentList;
        foreach(var a in new[]{"-nostdin","-hide_banner","-loglevel","error","-y"})args.Add(a);
        if(camera.Synthetic){foreach(var a in new[]{"-re","-f","lavfi","-i","testsrc2=size=640x360:rate=15"})args.Add(a);}
        else {if(source!.StartsWith("rtsp",StringComparison.OrdinalIgnoreCase)){args.Add("-rtsp_transport");args.Add("tcp");}args.Add("-i");args.Add(source);}
        foreach(var a in new[]{"-t","20","-an","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-r",Math.Clamp(effective.Fps??15,1,60).ToString(),"-fs","45000000","-movflags","+frag_keyframe+empty_moov","-g","15",file})args.Add(a);
        try
        {
            if(!process.Start())throw new InvalidOperationException("FFmpeg did not start.");
            var stderr=process.StandardError.ReadToEndAsync(ct);var stdout=process.StandardOutput.ReadToEndAsync(ct);
            var deadline=DateTime.UtcNow.AddSeconds(35);var heartbeat=DateTime.UtcNow;
            while(!process.HasExited&&DateTime.UtcNow<deadline)
            {
                if(!started&&File.Exists(file)&&new FileInfo(file).Length>4096)
                {await Send(client,HttpMethod.Post,root+"executions",new{executionId=id,scheduleId=effective.ScheduleId,scheduleVersion=effective.ScheduleVersion},ct);started=true;}
                if(started&&DateTime.UtcNow>=heartbeat)
                {
                    using var response=await client.PostAsync(path+"/heartbeat",null,ct);response.EnsureSuccessStatusCode();
                    var lease=await response.Content.ReadFromJsonAsync<Envelope<Lease>>(cancellationToken:ct);
                    if(lease?.Data?.ShouldContinue!=true)throw new InvalidOperationException("Recording lease ended.");
                    heartbeat=DateTime.UtcNow.AddSeconds(10);
                }
                await Task.Delay(500,ct);
            }
            if(!process.HasExited||process.ExitCode!=0||!File.Exists(file)||new FileInfo(file).Length<=4096)
                throw new InvalidOperationException("FFmpeg failed or produced no video.");
            await stderr;await stdout;
            if(!started){await Send(client,HttpMethod.Post,root+"executions",new{executionId=id,scheduleId=effective.ScheduleId,scheduleVersion=effective.ScheduleVersion},ct);started=true;}
            await using var stream=File.OpenRead(file);
            using var body=new MultipartFormDataContent();
            using var content=new StreamContent(stream);content.Headers.ContentType=new("video/mp4");body.Add(content,"file",id.ToString("N")+".mp4");
            using var upload=await client.PostAsync(path+"/video",body,ct);upload.EnsureSuccessStatusCode();
        }
        catch
        {
            if(started&&!ct.IsCancellationRequested)
            {
                try{await Send(client,HttpMethod.Put,path+"/finish",new{status="Failed",error="Recording or upload did not complete.",mediaAssetId=(Guid?)null},ct);}catch{ /* lease expires on server */ }
            }
            throw;
        }
        finally
        {
            try{if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync(CancellationToken.None);}}catch(InvalidOperationException){}
            if(File.Exists(file))File.Delete(file);
        }
    }
}
