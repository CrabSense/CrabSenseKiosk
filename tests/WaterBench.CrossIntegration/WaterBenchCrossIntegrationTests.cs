using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CrabSenseBE.Api.Controllers;
using CrabSenseBE.Api.Middleware;
using CrabSenseBE.Application.Interfaces;
using CrabSenseBE.Application.Services;
using CrabSenseBE.Domain.Entities;
using CrabSenseBE.Domain.Enums;
using CrabSenseBE.Domain.Interfaces;
using CrabSenseBE.Infrastructure.Persistence;
using CrabSenseBE.Infrastructure.Repositories;
using CrabSenseKiosk.Services.WaterBench;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SkiaSharp;
using Xunit;

namespace CrabSenseKiosk.CrossIntegration;

public sealed class WaterBenchCrossIntegrationTests
{
    private static readonly Uri Origin=new("https://water-bench-ci.test");
    private static readonly Uri BatchEndpoint=new(Origin,"/api/edge/water-bench/batch-events");
    private static readonly Uri MediaEndpoint=new(Origin,"/api/edge/water-bench/media");
    private const string Boot="0123456789abcdef",Batch="abcdef0123456789",Nh3Session="1111222233334444";
    private const string Master="000000000001",Slave="000000000002";
    private const string Secret="synthetic-kiosk-secret-only-for-ci",OtherSecret="synthetic-other-kiosk-secret-ci";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static string Connection()
    {
        var raw=Environment.GetEnvironmentVariable("WATER_BENCH_CI_DB")??throw new InvalidOperationException("Dedicated CI database required.");
        var c=new NpgsqlConnectionStringBuilder(raw);
        if(c.Host!="127.0.0.1"||c.Port!=55439||c.Database!="water_bench_ci"||c.Username!="water_bench_ci")
            throw new InvalidOperationException("Refusing non-test database.");
        return c.ConnectionString;
    }
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private sealed record Fixture(Guid Area,Guid OtherArea,Guid Station,Guid OtherStation,Guid Camera,
        Guid Admin,Guid Manager,Guid Staff,Guid System,Guid Outsider,Guid Kiosk);
    private static AppUser User(UserRole role,string name)=>new(){Username=name,Email=name+"@example.invalid",FullName=name,
        PasswordHash="not-a-login-password",Role=role};
    private static async Task<Fixture> Seed(string cs,DateTimeOffset origin)
    {
        await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options);
        await db.Database.MigrateAsync();
        // The service is fresh for this job; refuse leftover test or application evidence.
        Assert.Empty(await db.WaterBenchEvidence.ToListAsync());
        var area=new FarmingArea{Code="AREA-A91",Name="CI WaterBench A"};
        var other=new FarmingArea{Code="AREA-A92",Name="CI WaterBench B"};
        var admin=User(UserRole.FarmAdministrator,"ci_admin");admin.ManagedAreas.Add(area);
        var outsider=User(UserRole.FarmAdministrator,"ci_other");outsider.ManagedAreas.Add(other);
        var manager=User(UserRole.FarmManager,"ci_manager");var staff=User(UserRole.FarmStaff,"ci_staff");
        var system=User(UserRole.SystemAdministrator,"ci_system");
        db.AddRange(area,other,admin,outsider,manager,staff,system);await db.SaveChangesAsync();
        db.FarmManagerAssignments.Add(new(){ManagerUserId=manager.Id,FarmingAreaId=area.Id,AssignedByUserId=admin.Id});
        db.FarmStaffAssignments.Add(new(){StaffUserId=staff.Id,ManagerUserId=manager.Id,FarmingAreaId=area.Id,AssignedByUserId=admin.Id});
        var kiosk=new FarmKiosk{Code="KIOSK-991",FarmingAreaId=area.Id,Status="Online"};
        var otherKiosk=new FarmKiosk{Code="KIOSK-992",FarmingAreaId=other.Id,Status="Online"};
        var camera=new Device{DeviceCode="CI-WB-CAMERA",DeviceType="camera",FarmingAreaId=area.Id};
        db.AddRange(kiosk,otherKiosk,camera);await db.SaveChangesAsync();
        db.AddRange(new KioskCredential{KioskId=kiosk.Id,SecretHash=Hash(Secret)},
            new KioskCredential{KioskId=otherKiosk.Id,SecretHash=Hash(OtherSecret)});
        var profiles=new[]{"NO2","NO3","NH3"}.Select(a=>new WaterColorCalibration{
            FarmingAreaId=area.Id,CameraDeviceId=camera.Id,Analyte=a,KitName=CrabSenseBE.Api.Services.WaterBatchPlanning.Kit(a),
            ReagentLot="ci-fixture",Unit="mg/L "+a,ProtocolJson="{}",CurveJson="{}",ValidationJson="{}",
            CreatedBy=admin.Id,CreatedAt=origin.UtcDateTime.AddDays(-1),ValidUntil=DateTime.UtcNow.AddDays(1)}).ToArray();
        db.AddRange(profiles);await db.SaveChangesAsync();
        var station=new WaterBenchStation{KioskId=kiosk.Id,MasterHardwareId=Master,SlaveHardwareId=Slave,ConfigurationVersion=1,
            TargetsJson=JsonSerializer.Serialize(profiles.Select(p=>new CrabSenseBE.Api.Services.WaterBatchPlanning.Target(p.Analyte,p.Id)),Json),
            SampleSource="RAS_RETURN"};
        var otherStation=new WaterBenchStation{KioskId=otherKiosk.Id,MasterHardwareId="000000000003",SlaveHardwareId="000000000004"};
        db.AddRange(station,otherStation);await db.SaveChangesAsync();
        return new(area.Id,other.Id,station.Id,otherStation.Id,camera.Id,admin.Id,manager.Id,staff.Id,system.Id,outsider.Id,kiosk.Id);
    }
    // Test-only identity injection; real role attributes, CurrentUserService and FarmAreaScope
    // still execute. JWT signature/issuance and the full production host are not under test.
    public sealed class CiAuth(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder,
        IReadOnlyDictionary<Guid,UserRole> users):AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if(!Guid.TryParse(Request.Headers["X-CI-User"].ToString(),out var id)||!users.TryGetValue(id,out var role))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity=new ClaimsIdentity(new[]{new Claim(ClaimTypes.NameIdentifier,id.ToString()),new Claim(ClaimTypes.Role,role.ToString())},Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity),Scheme.Name)));
        }
    }
    private sealed class SelectedControllers:IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts,ControllerFeature feature)
        {
            var allowed=new[]{typeof(WaterBenchBatchEdgeController),typeof(WaterBenchMediaController),
                typeof(WaterBenchNh3EdgeController),typeof(WaterBenchReceiptController),typeof(WaterBenchBatchController),typeof(WaterBenchNh3Controller)};
            foreach(var c in feature.Controllers.Where(c=>!allowed.Contains(c.AsType())).ToArray())feature.Controllers.Remove(c);
        }
    }
    private sealed class PrivateStorage:IMediaStorageService
    {
        private readonly ConcurrentDictionary<string,byte[]> _files=new();
        public string ProviderName=>"GoogleDrive"; // only the interface is substituted; no Google credentials or network
        public int Uploads,Reads,PrivateChecks,Deletes;
        public bool CorruptNextRead;
        public int Count=>_files.Count;
        public async Task<MediaUploadResult> UploadAsync(Stream data,string fileName,string contentType,string category,CancellationToken ct=default,bool publicRead=false)
        {
            Assert.False(publicRead);Assert.Equal("image/jpeg",contentType);Assert.Contains("04_PhanTichNuoc",category);
            using var ms=new MemoryStream();await data.CopyToAsync(ms,ct);var bytes=ms.ToArray();
            string key="ci-only-"+Guid.NewGuid().ToString("N");Assert.True(_files.TryAdd(key,bytes));Interlocked.Increment(ref Uploads);
            return new(key,null,null,null,bytes.LongLength);
        }
        public Task EnsurePrivateAccessAsync(string key,CancellationToken ct=default)
        {Assert.True(_files.ContainsKey(key));Interlocked.Increment(ref PrivateChecks);return Task.CompletedTask;}
        public Task<Stream> DownloadAsync(string key,CancellationToken ct=default)
        {
            Interlocked.Increment(ref Reads);var bytes=_files[key].ToArray();
            if(CorruptNextRead){CorruptNextRead=false;bytes[bytes.Length/2]^=1;}
            return Task.FromResult<Stream>(new MemoryStream(bytes,writable:false));
        }
        public Task DeleteAsync(string key,CancellationToken ct=default)
        {Assert.True(_files.TryRemove(key,out _));Interlocked.Increment(ref Deletes);return Task.CompletedTask;}
        public Task<string> EnsureShareLinkAsync(string key,CancellationToken ct=default)=>throw new InvalidOperationException("Private capture must not be shared.");
    }
    private static TestServer Server(string cs,Fixture f,PrivateStorage storage)=>new(new WebHostBuilder()
        .ConfigureServices(s=>{
            s.AddLogging();s.AddHttpContextAccessor();
            s.AddDbContext<AppDbContext>(o=>o.UseNpgsql(cs));
            s.AddScoped<IUnitOfWork,UnitOfWork>();s.AddScoped<IKioskProvisioningService,KioskProvisioningService>();
            s.AddScoped<ICurrentUserService,CrabSenseBE.Api.Services.CurrentUserService>();
            s.AddSingleton<IMediaStorageService>(storage);
            s.AddSingleton<IReadOnlyDictionary<Guid,UserRole>>(new Dictionary<Guid,UserRole>{
                [f.Admin]=UserRole.FarmAdministrator,[f.Outsider]=UserRole.FarmAdministrator,
                [f.Manager]=UserRole.FarmManager,[f.Staff]=UserRole.FarmStaff,[f.System]=UserRole.SystemAdministrator});
            s.AddAuthentication("ci").AddScheme<AuthenticationSchemeOptions,CiAuth>("ci",_=>{});s.AddAuthorization();
            s.AddControllers().AddApplicationPart(typeof(WaterBenchBatchEdgeController).Assembly)
                .ConfigureApplicationPartManager(m=>m.FeatureProviders.Add(new SelectedControllers()));
        })
        .Configure(a=>{a.UseMiddleware<ExceptionMiddleware>();a.UseRouting();a.UseAuthentication();a.UseAuthorization();
            a.UseEndpoints(e=>e.MapControllers());}));
    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        var body=await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode==HttpStatusCode.OK,$"Unexpected HTTP {(int)response.StatusCode}: {body}");
        using var doc=JsonDocument.Parse(body);Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        return doc.RootElement.GetProperty("data").Clone();
    }
    private static Task<HttpResponseMessage> Post(HttpClient client,Uri url,object value,string key=Secret)
    {
        // Request disposed after SendAsync completes.
        return Send();
        async Task<HttpResponseMessage> Send()
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,url);
            request.Headers.Add("X-Kiosk-Key",key);request.Content=JsonContent.Create(value,value.GetType(),options:Json);
            return await client.SendAsync(request);
        }
    }
    private static Task<HttpResponseMessage> Get(HttpClient client,string path,Guid? user=null)
    {
        return Send();
        async Task<HttpResponseMessage> Send()
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,new Uri(Origin,path));
            if(user.HasValue)request.Headers.Add("X-CI-User",user.Value.ToString());
            return await client.SendAsync(request);
        }
    }
    private sealed class DropMediaResponse(HttpMessageHandler inner):DelegatingHandler(inner)
    {
        internal bool DropNextSuccess;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var response=await base.SendAsync(request,ct);
            if(DropNextSuccess&&request.RequestUri==MediaEndpoint&&response.StatusCode==HttpStatusCode.OK)
            {DropNextSuccess=false;response.Dispose();throw new HttpRequestException("Synthetic response loss after server commit.");}
            return response;
        }
    }
    private static byte[] Jpeg(SKColor color)
    {
        using var bitmap=new SKBitmap(32,32);bitmap.Erase(color);
        using var image=SKImage.FromBitmap(bitmap);using var data=image.Encode(SKEncodedImageFormat.Jpeg,90);return data.ToArray();
    }
    private static BenchBatchEvent SeedSpool(EvidenceSpool spool,Guid station,DateTimeOffset at)
    {
        var conn=Guid.NewGuid();var journal=new BatchObservationJournal(station,conn,Boot,spool);
        journal.Start($"BATCH START {Batch} mode=scheduled mask=7",null,at);
        var types=new[]{("NO2",Batch+"-1",1,300000,SKColors.Beige),("NO3",Batch+"-2",2,120000,SKColors.LightPink),
            ("NH3",Nh3Session,3,600000,SKColors.LightGreen)};
        foreach(var (analyte,sid,id,duration,color) in types)
        {
            journal.Session(JsonSerializer.SerializeToElement(new{batch_id=Batch,analyte,session_id=sid}),at.AddSeconds(1));
            if(analyte=="NH3")
                spool.Nh3Event(new(station,Guid.NewGuid(),conn,Boot,Batch,sid,"sample",at.AddSeconds(2),Master,Slave,
                    new Nh3SampleReading(500,550,7.5m,600,28.5m,3000)));
            journal.Reaction($"BATCH REACTION {Batch} {id} start=1000 duration={duration}",at.AddSeconds(3));
        }
        foreach(var (analyte,sid,id,duration,color) in types.OrderBy(x=>x.Item4))
        {
            var imageAt=at.AddMilliseconds(3000+duration);
            journal.Image(JsonSerializer.SerializeToElement(new{batch_id=Batch,analyte,session_id=sid,capture_id=id}),imageAt);
            if(analyte=="NH3")spool.Nh3Event(new(station,Guid.NewGuid(),conn,Boot,Batch,sid,"image_link",imageAt.AddMilliseconds(100),Master,Slave,
                Image:new Nh3ImageObservation(id,601500,601000)));
            var bytes=Jpeg(color);
            spool.Frame(new ReceivedFrame((uint)id,(uint)id,32,32,WireProtocol.Crc32(bytes),bytes,imageAt.AddMilliseconds(500)),
                new{synthetic=true},new BenchMediaIdentity(station,conn,Boot,Batch,analyte,sid,imageAt,Master,Slave));
        }
        journal.Status(JsonSerializer.SerializeToElement(new{batch_id=Batch,state=2,mask=7,started=7,reacting=0,imaged=7,drained=7}),at.AddSeconds(605));
        var q=new BenchSyncQueue(spool,station);
        return Assert.IsType<BenchBatchEvent>(q.Prepare(Assert.Single(q.Scan().Ready)));
    }
    [Fact]
    public async Task ScheduledThreeAnalytes_RealBackendAndPostgres_DuplicateLossRecoveryAndScope()
    {
        var cs=Connection();var at=DateTimeOffset.UtcNow.AddMinutes(-30);var f=await Seed(cs,at);
        var root=Path.Combine(Path.GetTempPath(),"water-bench-ci-"+Guid.NewGuid().ToString("N"));
        var storage=new PrivateStorage();using var server=Server(cs,f,storage);
        using var direct=server.CreateClient();direct.BaseAddress=Origin;
        using var drop=new DropMediaResponse(server.CreateHandler());using var client=new HttpClient(drop){BaseAddress=Origin};
        try
        {
            var spool=new EvidenceSpool(root,4*1024*1024,200);var start=SeedSpool(spool,f.Station,at);
            var q=new BenchSyncQueue(spool,f.Station);
            using(var rejected=await Post(direct,BatchEndpoint,start,"invalid-ci-key"))Assert.Equal(HttpStatusCode.Unauthorized,rejected.StatusCode);
            using(var rejected=await Post(direct,BatchEndpoint,start,OtherSecret))Assert.Equal(HttpStatusCode.NotFound,rejected.StatusCode);
            using(var rejected=await Post(direct,BatchEndpoint,start with{EventId=Guid.NewGuid(),Sequence=2,Type="session",Analyte="NO2",SessionId=Batch+"-1"}))
                Assert.Equal(HttpStatusCode.Conflict,rejected.StatusCode);
            // Two independent HTTP scopes and DB transactions race the same event.
            var results=await Task.WhenAll(Post(direct,BatchEndpoint,start),Post(direct,BatchEndpoint,start));
            Guid firstReceipt;
            using(results[0])using(results[1])
            {
                var a=await Data(results[0]);var b=await Data(results[1]);
                firstReceipt=a.GetProperty("receiptId").GetGuid();Assert.Equal(firstReceipt,b.GetProperty("receiptId").GetGuid());
                Assert.NotEqual(a.GetProperty("duplicate").GetBoolean(),b.GetProperty("duplicate").GetBoolean());
            }
            using(var changed=await Post(direct,BatchEndpoint,start with{Mode="manual"}))Assert.Equal(HttpStatusCode.Conflict,changed.StatusCode);
            // Normal sender receives the same receipt and syncs all timeline and NH3 prerequisites.
            for(int n=0;n<13;n++)
            {
                var next=q.Scan().Ready.FirstOrDefault(x=>x.Kind=="batch")??q.Scan().Ready.FirstOrDefault(x=>x.Kind=="nh3");
                if(next is null)break;
                var result=await BackendBenchBatchSync.SendNext(client,BatchEndpoint,Secret,q,default,true,true);
                Assert.True(result.Acknowledged,result.State+" HTTP "+result.HttpStatus);
            }
            Assert.Equal(13,q.Scan().Acknowledged);Assert.Equal(3,q.Scan().Pending);
            var media=Assert.Single(q.Scan().Ready);Assert.Equal("media",media.Kind);
            // Storage corrupt readback must roll back and clean up, without ACK.
            storage.CorruptNextRead=true;
            var failure=await BackendBenchBatchSync.SendNext(client,BatchEndpoint,Secret,q,default,true,true);
            Assert.Equal("backend_conflict",failure.State);Assert.Equal(1,storage.Deletes);Assert.Equal(0,storage.Count);
            Assert.Equal(3,q.Scan().Pending);
            // Server commits the valid capture, but the kiosk loses the response.
            drop.DropNextSuccess=true;
            var lost=await BackendBenchBatchSync.SendNext(client,BatchEndpoint,Secret,q,default,true,true);
            Assert.Equal("transport_unavailable",lost.State);Assert.Equal(1,storage.Count);Assert.Equal(3,q.Scan().Pending);
            q=new BenchSyncQueue(new EvidenceSpool(root,4*1024*1024,200),f.Station);
            Assert.Equal(media.EventId,Assert.Single(q.Scan().Ready).EventId);
            Assert.True((await BackendBenchBatchSync.SendNext(client,BatchEndpoint,Secret,q,default,true,true)).Acknowledged);
            Assert.Equal(2,storage.Uploads); // corrupt attempt + committed image; retry did not re-upload
            for(int n=0;n<2;n++)Assert.True((await BackendBenchBatchSync.SendNext(client,BatchEndpoint,Secret,q,default,true,true)).Acknowledged);
            Assert.Equal(0,q.Scan().Pending);Assert.Equal(16,q.Scan().Acknowledged);
            Assert.Equal(4,storage.Uploads);Assert.Equal(4,storage.Reads);Assert.Equal(4,storage.PrivateChecks);Assert.Equal(3,storage.Count);
            string path=$"/api/areas/{f.Area}/water-bench/batches/{f.Station}/{Boot}/{Batch}/receipts";
            using(var unauth=await Get(direct,path))Assert.Equal(HttpStatusCode.Unauthorized,unauth.StatusCode);
            using(var denied=await Get(direct,path,f.System))Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
            using(var denied=await Get(direct,path,f.Outsider))Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
            using(var denied=await Get(direct,path.Replace(f.Area.ToString(),f.OtherArea.ToString()),f.Admin))Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
            using(var wrongStation=await Get(direct,path.Replace(f.Station.ToString(),f.OtherStation.ToString()),f.Admin))
                Assert.Equal(HttpStatusCode.NotFound,wrongStation.StatusCode);
            foreach(var user in new[]{f.Manager,f.Staff})
            {using var response=await Get(direct,path,user);Assert.Equal(16,(await Data(response)).GetProperty("total").GetInt32());}
            using var remoteResponse=await Get(direct,path,f.Admin);var remote=await Data(remoteResponse);
            Assert.Equal(16,remote.GetProperty("total").GetInt32());Assert.Equal(11,remote.GetProperty("timelineCount").GetInt32());
            Assert.Equal(3,remote.GetProperty("mediaCount").GetInt32());Assert.Equal(2,remote.GetProperty("nh3Count").GetInt32());
            Assert.False(remote.GetProperty("chemicalResultValidated").GetBoolean());Assert.False(remote.GetProperty("driveContentRechecked").GetBoolean());
            var local=JsonSerializer.SerializeToElement(q.BatchReceipts(Boot,Batch),Json);
            Assert.True(local.GetProperty("inventoryComplete").GetBoolean());
            var localItems=local.GetProperty("items").EnumerateArray().ToDictionary(x=>x.GetProperty("eventId").GetGuid());
            foreach(var item in remote.GetProperty("items").EnumerateArray())
            {
                var id=item.GetProperty("eventId").GetGuid();var localItem=localItems[id];
                Assert.Equal(item.GetProperty("receiptId").GetGuid(),localItem.GetProperty("receiptId").GetGuid());
                Assert.Equal(item.GetProperty("kind").GetString(),localItem.GetProperty("kind").GetString());
                if(item.GetProperty("kind").GetString()=="media")
                {
                    Assert.Equal(item.GetProperty("mediaId").GetGuid(),localItem.GetProperty("mediaId").GetGuid());
                    Assert.Equal(item.GetProperty("sha256").GetString(),localItem.GetProperty("sha256").GetString());
                    Assert.True(item.GetProperty("privateMediaMetadataPresent").GetBoolean());
                }
            }
            Assert.Equal(firstReceipt,localItems[start.EventId].GetProperty("receiptId").GetGuid());
            await using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options);
            Assert.Equal(16,await db.WaterBenchEvidence.CountAsync(x=>x.StationId==f.Station));
            Assert.Equal(3,await db.MediaAssets.CountAsync(x=>x.DeviceId==f.Camera&&!x.IsShared));
            Assert.Empty(await db.WaterBenchResultAssessments.ToListAsync());Assert.Empty(await db.WaterAnalysisRuns.Where(x=>x.FarmingAreaId==f.Area).ToListAsync());
            Assert.Equal(3,Directory.GetFiles(root,"*.jpg").Length);
            // Revoked kiosk cannot obtain even an existing receipt.
            var kiosk=await db.FarmKiosks.SingleAsync(x=>x.Id==f.Kiosk);kiosk.Status="Revoked";await db.SaveChangesAsync();
            using var revoked=await Post(direct,BatchEndpoint,start);Assert.Equal(HttpStatusCode.Forbidden,revoked.StatusCode);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
