using CrabSenseKiosk.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace CrabSenseKiosk.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class HealthController : ControllerBase
    {
        private readonly ILogger<HealthController> _logger;

        public HealthController(ILogger<HealthController> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Kiểm tra trạng thái API
        /// </summary>
        [HttpGet]
        public ActionResult<ApiResponse<object>> GetHealth()
        {
            var healthInfo = new
            {
                Status = "Healthy",
                Timestamp = DateTime.UtcNow,
                Version = "1.0.0",
                Service = "CrabSense Kiosk API"
            };

            return Ok(ApiResponse<object>.SuccessResponse(healthInfo, "API đang hoạt động bình thường"));
        }

        /// <summary>
        /// Kiểm tra chi tiết trạng thái hệ thống
        /// </summary>
        [HttpGet("detailed")]
        public ActionResult<ApiResponse<object>> GetDetailedHealth()
        {
            var detailedInfo = new
            {
                Status = "Healthy",
                Timestamp = DateTime.UtcNow,
                Version = "1.0.0",
                Service = "CrabSense Kiosk API",
                Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                MachineName = Environment.MachineName,
                OSVersion = Environment.OSVersion.ToString(),
                ProcessorCount = Environment.ProcessorCount,
                SystemUptime = TimeSpan.FromMilliseconds(Environment.TickCount64)
            };

            return Ok(ApiResponse<object>.SuccessResponse(detailedInfo));
        }
    }
}
