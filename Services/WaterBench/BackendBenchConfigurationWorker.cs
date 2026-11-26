using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CrabSenseKiosk.Services.WaterBench;

internal sealed record BackendBenchTarget(string Analyte,Guid CalibrationId);
internal sealed record BackendBenchConfiguration(Guid StationId,string MasterHardwareId,string SlaveHardwareId,
    int ConfigurationVersion,bool Enabled,bool ConfiguredEnabled,bool ProfilesReady,string[] Times,int GraceMinutes,
    BackendBenchTarget[] Targets,string SampleSource,string Timezone,string Execution,bool PhysicalDispatch)
{
    internal string ValidateAndHash()
    {
        if(Times is null||Targets is null||Targets.Any(t=>t is null)||
            (ConfigurationVersion==0?(ConfiguredEnabled||Targets.Length!=0||Times.Length!=0):
            (Targets.Length!=3||Targets.Any(t=>t.Analyte is not ("NO2" or "NO3" or "NH3")||t.CalibrationId==Guid.Empty)||
             Targets.Select(t=>t.Analyte).Distinct().Count()!=3||Targets.Select(t=>t.CalibrationId).Distinct().Count()!=3||
             SampleSource is not ("RAS_RETURN" or "RETURN_TANK" or "BIO_TANK" or "SETTLING_TANK" or "INLET"))))
            throw new ArgumentException("Invalid backend targets.");
        _=ScheduleCalendar.Validate(new(ConfiguredEnabled,Times,GraceMinutes));
        // Effective enabled/profilesReady may change without a configuration edit (e.g. calibration expiry).
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new {ConfiguredEnabled,times=Times.OrderBy(t=>t,StringComparer.Ordinal),
            GraceMinutes,targets=Targets.OrderBy(t=>t.Analyte,StringComparer.Ordinal),SampleSource});
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
public sealed class BackendBenchConfigurationWorker(IHttpClientFactory clients,IOptions<EdgeGatewayOptions> gateway,
    IOptions<WaterBenchOptions> options,BenchSchedule schedule,ILogger<BackendBenchConfigurationWorker> log):BackgroundService
{
    private sealed record Response(bool Success,BackendBenchConfiguration? Data);
    internal static Uri? Endpoint(string root)
    {
        if(!Uri.TryCreate(root.TrimEnd('/')+"/",UriKind.Absolute,out var uri)||uri.Scheme!="https"||
            !string.IsNullOrEmpty(uri.UserInfo)||!string.IsNullOrEmpty(uri.Query)||!string.IsNullOrEmpty(uri.Fragment))return null;
        return new Uri(uri,"api/edge/water-bench/configuration");
    }
    private async Task Poll(CancellationToken ct)
    {
        var endpoint=Endpoint(gateway.Value.BackendUrl);
        if(endpoint is null||string.IsNullOrWhiteSpace(gateway.Value.KioskSecret)||options.Value.BackendStationId==Guid.Empty)
        {schedule.BackendUnavailable("backend_configuration_not_provisioned");return;}
        using var client=clients.CreateClient("water-bench-config");
        client.Timeout=TimeSpan.FromSeconds(8);
        client.MaxResponseContentBufferSize=65536;
        using var request=new HttpRequestMessage(HttpMethod.Get,endpoint);
        request.Headers.Add("X-Kiosk-Key",gateway.Value.KioskSecret.Trim());
        using var result=await client.SendAsync(request,ct);
        if(!result.IsSuccessStatusCode){schedule.BackendUnavailable("backend_configuration_rejected");return;}
        var payload=await result.Content.ReadFromJsonAsync<Response>(cancellationToken:ct);
        if(payload is null||!payload.Success||payload.Data is null)
        {schedule.BackendUnavailable("no_active_backend_station");return;}
        schedule.ApplyBackend(payload.Data,DateTimeOffset.UtcNow);
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try{await Poll(ct);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex)
            {schedule.BackendUnavailable("backend_configuration_unreachable");log.LogWarning("Water bench config sync failed: {Type}",ex.GetType().Name);}
            try{await Task.Delay(TimeSpan.FromSeconds(10),ct);}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
        }
    }
}
