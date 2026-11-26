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
    [HttpGet("command/{requestId:guid}")]
    public IActionResult CommandStatus(Guid requestId)
    {
        if(!Allowed())return Unauthorized();
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
