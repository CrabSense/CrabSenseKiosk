using System.Net;
using System.Security.Cryptography;
using System.Text;
using CrabSenseKiosk.Services.WaterBench;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CrabSenseKiosk.Controllers;

[ApiController]
[Route("api/water-bench")]
public sealed class WaterBenchController : ControllerBase
{
    private readonly WaterBenchOptions _options;
    private readonly WaterBenchSession _session;
    private readonly BenchSchedule _schedule;
    public WaterBenchController(IOptions<WaterBenchOptions> options,WaterBenchSession session,BenchSchedule schedule)
        =>(_options,_session,_schedule)=(options.Value,session,schedule);
    private bool Allowed()
    {
        var ip=HttpContext.Connection.RemoteIpAddress;
        var key=_options.LocalOperatorKey;
        if(!_options.Enabled||ip is null||!IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6?ip.MapToIPv4():ip)||
            key.Length<32||Request.Headers.ContainsKey("Origin"))return false;
        var provided=Request.Headers["X-Water-Bench-Key"].ToString();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key),Encoding.UTF8.GetBytes(provided));
    }
    [HttpGet("status")]
    public IActionResult Status()=>Allowed()?Ok(_session.Snapshot()):Unauthorized();
    [HttpGet("sync/status")]
    public IActionResult SyncStatus()
    {
        if(!Allowed())return Unauthorized();
        Response.Headers.CacheControl="no-store";
        return Ok(_session.SyncStatus());
    }
    [HttpGet("sync/batches")]
    public IActionResult SyncBatches([FromQuery]int skip=0,[FromQuery]int take=25)
    {
        if(!Allowed())return Unauthorized();
        if(skip<0||skip>100000||take is <1 or >50)return BadRequest(new{message="Invalid page."});
        Response.Headers.CacheControl="no-store";
        return Ok(_session.SyncQueue.Batches(skip,take));
    }
    [HttpGet("sync/batches/{bootId}/{batchId}/receipts")]
    public IActionResult SyncReceipts(string bootId,string batchId)
    {
        if(!Allowed())return Unauthorized();
        if(!WireProtocol.BatchId(bootId)||!WireProtocol.BatchId(batchId))return BadRequest(new{message="Invalid batch identity."});
        Response.Headers.CacheControl="no-store";
        var data=_session.SyncQueue.BatchReceipts(bootId,batchId);
        return data is null?NotFound():Ok(data);
    }
    [HttpGet("schedule")]
    public IActionResult Schedule()=>Allowed()?Ok(_schedule.Snapshot(DateTimeOffset.UtcNow)):Unauthorized();
    [HttpPut("schedule")]
    [RequestSizeLimit(4096)]
    public IActionResult ConfigureSchedule(BenchScheduleSettings settings)
    {
        if(!Allowed())return Unauthorized();
        return _schedule.Configure(settings,DateTimeOffset.UtcNow,out var message)?Ok(new{message}):BadRequest(new{message});
    }
    [HttpPost("schedule/approve")]
    [RequestSizeLimit(1024)]
    public IActionResult ApproveSchedule(BenchScheduleApproval approval)
    {
        if(!Allowed())return Unauthorized();
        return _schedule.Approve(approval.SlotId,DateTimeOffset.UtcNow,out var message)?Accepted(new{message}):Conflict(new{message});
    }
    [HttpPost("recovery/confirm")]
    [RequestSizeLimit(2048)]
    public IActionResult ConfirmRecovery(BenchRecoveryRequest request)
    {
        if(!Allowed())return Unauthorized();
        return _session.TryRecover(request,out var message)?Ok(new{message}):Conflict(new{message});
    }
    [HttpGet("commands")]
    public IActionResult Commands([FromQuery]int skip=0,[FromQuery]int take=25)
    {
        if(!Allowed())return Unauthorized();
        if(skip<0||skip>100000||take is <1 or >50)return BadRequest(new{message="Invalid page."});
        Response.Headers.CacheControl="no-store";
        return Ok(_session.Commands(skip,take));
    }
    [HttpGet("command/{requestId:guid}")]
    public IActionResult CommandStatus(Guid requestId)
    {
        if(!Allowed())return Unauthorized();
        Response.Headers.CacheControl="no-store";
        var status=_session.CommandStatus(requestId);
        return status is null?NotFound():Ok(status);
    }
    [HttpPost("command")]
    [RequestSizeLimit(4096)]
    public IActionResult Command(BenchCommand request)
    {
        if(!Allowed())return Unauthorized();
        if(_options.UseBackendSchedule&&request.Action=="scheduled")
            return Conflict(new{message="Approve a synchronized schedule slot instead."});
        return _session.TryCommand(request,out var message)?Accepted(new{message}):Conflict(new{message});
    }
}
