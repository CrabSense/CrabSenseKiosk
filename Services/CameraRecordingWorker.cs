using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace CrabSenseKiosk.Services;

public sealed class CameraRecordingWorker(IConfiguration config, IHttpClientFactory factory,
    ILogger<CameraRecordingWorker> logger) : BackgroundService
{
    private sealed record Envelope<T>(bool Success,T? Data);
    private sealed record Effective(Guid? ScheduleId,int ScheduleVersion,bool ShouldRecord,string? StreamUrl,int? Fps,string? VideoQuality);
    private sealed record Lease(bool ShouldContinue);
    private sealed record Camera(Guid Id,string Key,string? Source,bool Synthetic);
    private sealed record Pending(Guid DeviceId,Guid ExecutionId);
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
        var folder=Path.Combine(config["CameraRecording:SpoolDirectory"]??Path.Combine(AppContext.BaseDirectory,"recording-spool"),camera.Id.ToString("N"));
        Directory.CreateDirectory(folder);
        if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        while(!ct.IsCancellationRequested)
        {
            try
            {
                await Recover(client,root,camera,folder,ct);
                var effective=await Get<Effective>(client,root+"configuration",ct);
                if(effective.ShouldRecord&&effective.ScheduleId.HasValue)
                    await Record(client,root,camera,effective,folder,ct);
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
    private async Task Record(HttpClient client,string root,Camera camera,Effective effective,string folder,CancellationToken ct)
    {
        var source=camera.Source??effective.StreamUrl;
        if(!camera.Synthetic&&(!Uri.TryCreate(source,UriKind.Absolute,out var uri)||
            uri.Scheme is not ("rtsp" or "rtsps" or "http" or "https")))
            throw new InvalidOperationException("Camera source not configured.");
        var id=Guid.NewGuid();
        var file=Path.Combine(folder,id.ToString("N")+".mp4");
        var journal=Path.Combine(folder,id.ToString("N")+".json");
        var path=root+"executions/"+id.ToString("D");
        var started=false;var captured=false;
        using var process=new Process{StartInfo=new ProcessStartInfo(config["CameraRecording:FfmpegPath"]??"ffmpeg")
            {UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true}};
        var args=process.StartInfo.ArgumentList;
        foreach(var a in new[]{"-nostdin","-hide_banner","-loglevel","error","-y"})args.Add(a);
        if(camera.Synthetic){foreach(var a in new[]{"-re","-f","lavfi","-i","testsrc2=size=640x360:rate=15"})args.Add(a);}
        else {if(source!.StartsWith("rtsp",StringComparison.OrdinalIgnoreCase)){args.Add("-rtsp_transport");args.Add("tcp");}args.Add("-i");args.Add(source);}
        var seconds=Math.Clamp(config.GetValue("CameraRecording:SegmentSeconds",20),3,20);
        var height=effective.VideoQuality switch {"1080p"=>1080,"720p"=>720,"480p"=>480,_=>0};
        if(height>0){args.Add("-vf");args.Add($"scale=-2:{height}");}
        foreach(var a in new[]{"-t",seconds.ToString(),"-an","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-r",Math.Clamp(effective.Fps??15,1,120).ToString(),"-fs","45000000","-movflags","+frag_keyframe+empty_moov","-g","15",file})args.Add(a);
        try
        {
            if(!process.Start())throw new InvalidOperationException("FFmpeg did not start.");
            var stderr=process.StandardError.ReadToEndAsync(ct);var stdout=process.StandardOutput.ReadToEndAsync(ct);
            var deadline=DateTime.UtcNow.AddSeconds(seconds+15);var heartbeat=DateTime.UtcNow;
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
            // Persist a private retry journal before calling the network. One pending clip per camera
            // bounds disk growth and survives a process restart or an ambiguous upload response.
            await File.WriteAllTextAsync(journal+".tmp",JsonSerializer.Serialize(new Pending(camera.Id,id)),ct);
            File.Move(journal+".tmp",journal,overwrite:true);captured=true;
            await UploadPending(client,root,camera,folder,new Pending(camera.Id,id),ct);
        }
        catch
        {
            if(started&&!captured&&!ct.IsCancellationRequested)
            {
                try{await Send(client,HttpMethod.Put,path+"/finish",new{status="Failed",error="Recording or upload did not complete.",mediaAssetId=(Guid?)null},ct);}catch{ /* lease expires on server */ }
            }
            throw;
        }
        finally
        {
            try{if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync(CancellationToken.None);}}catch(InvalidOperationException){}
            if(!captured&&File.Exists(file))File.Delete(file);
            if(File.Exists(journal+".tmp"))File.Delete(journal+".tmp");
        }
    }
    private static async Task Recover(HttpClient client,string root,Camera camera,string folder,CancellationToken ct)
    {
        foreach(var journal in Directory.EnumerateFiles(folder,"*.json").OrderBy(x=>x))
        {
            var pending=JsonSerializer.Deserialize<Pending>(await File.ReadAllTextAsync(journal,ct))
                ??throw new InvalidOperationException("Invalid recording journal.");
            if(pending.DeviceId!=camera.Id||pending.ExecutionId==Guid.Empty||
                Path.GetFileNameWithoutExtension(journal)!=pending.ExecutionId.ToString("N"))
                throw new InvalidOperationException("Recording journal identity mismatch.");
            await UploadPending(client,root,camera,folder,pending,ct);
        }
        // Unjournaled partial captures cannot be claimed as complete; retain for diagnostics
        // for one day, then clean only this worker's per-camera spool.
        foreach(var file in Directory.EnumerateFiles(folder,"*.mp4"))
            if(!File.Exists(Path.ChangeExtension(file,".json"))&&File.GetLastWriteTimeUtc(file)<DateTime.UtcNow.AddDays(-1))
                File.Delete(file);
    }
    private static async Task UploadPending(HttpClient client,string root,Camera camera,string folder,Pending pending,CancellationToken ct)
    {
        var stem=Path.Combine(folder,pending.ExecutionId.ToString("N"));
        var path=root+"executions/"+pending.ExecutionId.ToString("D");
        await Send(client,HttpMethod.Post,path+"/captured",new{},ct);
        await using(var stream=File.OpenRead(stem+".mp4"))
        {
            using var body=new MultipartFormDataContent();
            using var content=new StreamContent(stream);
            content.Headers.ContentType=new("video/mp4");body.Add(content,"file",pending.ExecutionId.ToString("N")+".mp4");
            using var upload=await client.PostAsync(path+"/video",body,ct);upload.EnsureSuccessStatusCode();
        }
        File.Delete(stem+".mp4");File.Delete(stem+".json");
    }
}
