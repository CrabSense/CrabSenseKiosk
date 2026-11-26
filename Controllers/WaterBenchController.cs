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
    public WaterBenchController(IOptions<WaterBenchOptions> options,WaterBenchSession session)
        =>(_options,_session)=(options.Value,session);
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
    [HttpPost("command")]
    [RequestSizeLimit(4096)]
    public IActionResult Command(BenchCommand request)
    {
        if(!Allowed())return Unauthorized();
        return _session.TryCommand(request,out var message)?Accepted(new{message}):Conflict(new{message});
    }
}
