using CrabSenseKiosk.DTOs;
using CrabSenseKiosk.Services;
using Microsoft.AspNetCore.Mvc;

namespace CrabSenseKiosk.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AlertController : ControllerBase
    {
        private readonly IAlertService _alertService;
        private readonly ILogger<AlertController> _logger;

        public AlertController(IAlertService alertService, ILogger<AlertController> logger)
        {
            _alertService = alertService;
            _logger = logger;
        }

        /// <summary>
        /// Lấy danh sách cảnh báo đang hoạt động (24h gần nhất)
        /// </summary>
        [HttpGet("active")]
        public async Task<ActionResult<ApiResponse<List<string>>>> GetActiveAlerts()
        {
            try
            {
                var alerts = await _alertService.GetActiveAlertsAsync();
                return Ok(ApiResponse<List<string>>.SuccessResponse(alerts));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting active alerts");
                return StatusCode(500, ApiResponse<List<string>>.ErrorResponse(
                    "Đã xảy ra lỗi khi lấy danh sách cảnh báo"
                ));
            }
        }
    }
}
