using CrabSenseKiosk.DTOs;
using CrabSenseKiosk.Services;
using Microsoft.AspNetCore.Mvc;

namespace CrabSenseKiosk.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WaterQualityController : ControllerBase
    {
        private readonly IWaterQualityService _waterQualityService;
        private readonly IAlertService _alertService;
        private readonly ILogger<WaterQualityController> _logger;

        public WaterQualityController(
            IWaterQualityService waterQualityService,
            IAlertService alertService,
            ILogger<WaterQualityController> logger)
        {
            _waterQualityService = waterQualityService;
            _alertService = alertService;
            _logger = logger;
        }

        /// <summary>
        /// Tạo một bản đọc chất lượng nước mới
        /// </summary>
        [HttpPost]
        public async Task<ActionResult<ApiResponse<WaterQualityReadingDto>>> CreateReading(
            [FromBody] CreateWaterQualityReadingDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ApiResponse<WaterQualityReadingDto>.ErrorResponse(
                        "Dữ liệu không hợp lệ",
                        ModelState.Values.SelectMany(v => v.Errors.Select(e => e.ErrorMessage)).ToList()
                    ));
                }

                var reading = await _waterQualityService.CreateReadingAsync(dto);

                // Process alerts if anomaly detected
                if (reading.IsAnomaly)
                {
                    await _alertService.ProcessWaterQualityAlert(reading.Id);
                }

                return Ok(ApiResponse<WaterQualityReadingDto>.SuccessResponse(
                    reading,
                    "Tạo bản đọc thành công"
                ));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating water quality reading");
                return StatusCode(500, ApiResponse<WaterQualityReadingDto>.ErrorResponse(
                    "Đã xảy ra lỗi khi tạo bản đọc"
                ));
            }
        }

        /// <summary>
        /// Lấy thông tin bản đọc theo ID
        /// </summary>
        [HttpGet("{id}")]
        public async Task<ActionResult<ApiResponse<WaterQualityReadingDto>>> GetReadingById(int id)
        {
            try
            {
                var reading = await _waterQualityService.GetReadingByIdAsync(id);
                if (reading == null)
                {
                    return NotFound(ApiResponse<WaterQualityReadingDto>.ErrorResponse(
                        "Không tìm thấy bản đọc"
                    ));
                }

                return Ok(ApiResponse<WaterQualityReadingDto>.SuccessResponse(reading));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting reading by id: {Id}", id);
                return StatusCode(500, ApiResponse<WaterQualityReadingDto>.ErrorResponse(
                    "Đã xảy ra lỗi khi lấy thông tin bản đọc"
                ));
            }
        }

        /// <summary>
        /// Lấy danh sách bản đọc với phân trang và bộ lọc
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<ApiResponse<PagedResponse<WaterQualityReadingDto>>>> GetReadings(
            [FromQuery] WaterQualityQueryDto query)
        {
            try
            {
                var result = await _waterQualityService.GetReadingsAsync(query);
                return Ok(ApiResponse<PagedResponse<WaterQualityReadingDto>>.SuccessResponse(result));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting readings");
                return StatusCode(500, ApiResponse<PagedResponse<WaterQualityReadingDto>>.ErrorResponse(
                    "Đã xảy ra lỗi khi lấy danh sách bản đọc"
                ));
            }
        }

        /// <summary>
        /// Lấy các bản đọc mới nhất
        /// </summary>
        [HttpGet("latest")]
        public async Task<ActionResult<ApiResponse<List<WaterQualityReadingDto>>>> GetLatestReadings(
            [FromQuery] int count = 10)
        {
            try
            {
                var readings = await _waterQualityService.GetLatestReadingsAsync(count);
                return Ok(ApiResponse<List<WaterQualityReadingDto>>.SuccessResponse(readings));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting latest readings");
                return StatusCode(500, ApiResponse<List<WaterQualityReadingDto>>.ErrorResponse(
                    "Đã xảy ra lỗi khi lấy danh sách bản đọc mới nhất"
                ));
            }
        }

        /// <summary>
        /// Lấy thống kê chất lượng nước
        /// </summary>
        [HttpGet("statistics")]
        public async Task<ActionResult<ApiResponse<WaterQualityStatisticsDto>>> GetStatistics(
            [FromQuery] DateTime? startDate,
            [FromQuery] DateTime? endDate,
            [FromQuery] int? sensorId)
        {
            try
            {
                var stats = await _waterQualityService.GetStatisticsAsync(startDate, endDate, sensorId);
                return Ok(ApiResponse<WaterQualityStatisticsDto>.SuccessResponse(stats));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting statistics");
                return StatusCode(500, ApiResponse<WaterQualityStatisticsDto>.ErrorResponse(
                    "Đã xảy ra lỗi khi lấy thống kê"
                ));
            }
        }

        /// <summary>
        /// Xóa bản đọc
        /// </summary>
        [HttpDelete("{id}")]
        public async Task<ActionResult<ApiResponse<bool>>> DeleteReading(int id)
        {
            try
            {
                var result = await _waterQualityService.DeleteReadingAsync(id);
                if (!result)
                {
                    return NotFound(ApiResponse<bool>.ErrorResponse(
                        "Không tìm thấy bản đọc"
                    ));
                }

                return Ok(ApiResponse<bool>.SuccessResponse(true, "Xóa bản đọc thành công"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting reading: {Id}", id);
                return StatusCode(500, ApiResponse<bool>.ErrorResponse(
                    "Đã xảy ra lỗi khi xóa bản đọc"
                ));
            }
        }
    }
}
