using CrabSenseKiosk.DTOs;
using CrabSenseKiosk.Services;
using Microsoft.AspNetCore.Mvc;

namespace CrabSenseKiosk.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SensorController : ControllerBase
    {
        private readonly ISensorService _sensorService;
        private readonly ILogger<SensorController> _logger;

        public SensorController(ISensorService sensorService, ILogger<SensorController> logger)
        {
            _sensorService = sensorService;
            _logger = logger;
        }

        /// <summary>
        /// Tạo sensor mới
        /// </summary>
        [HttpPost]
        public async Task<ActionResult<ApiResponse<SensorDto>>> CreateSensor([FromBody] CreateSensorDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ApiResponse<SensorDto>.ErrorResponse(
                        "Dữ liệu không hợp lệ",
                        ModelState.Values.SelectMany(v => v.Errors.Select(e => e.ErrorMessage)).ToList()
                    ));
                }

                var sensor = await _sensorService.CreateSensorAsync(dto);
                return Ok(ApiResponse<SensorDto>.SuccessResponse(sensor, "Tạo sensor thành công"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating sensor");
                return StatusCode(500, ApiResponse<SensorDto>.ErrorResponse(
                    "Đã xảy ra lỗi khi tạo sensor"
                ));
            }
        }

        /// <summary>
        /// Lấy thông tin sensor theo ID
        /// </summary>
        [HttpGet("{id}")]
        public async Task<ActionResult<ApiResponse<SensorDto>>> GetSensorById(int id)
        {
            try
            {
                var sensor = await _sensorService.GetSensorByIdAsync(id);
                if (sensor == null)
                {
                    return NotFound(ApiResponse<SensorDto>.ErrorResponse("Không tìm thấy sensor"));
                }

                return Ok(ApiResponse<SensorDto>.SuccessResponse(sensor));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting sensor by id: {Id}", id);
                return StatusCode(500, ApiResponse<SensorDto>.ErrorResponse(
                    "Đã xảy ra lỗi khi lấy thông tin sensor"
                ));
            }
        }

        /// <summary>
        /// Lấy tất cả sensors
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<ApiResponse<List<SensorDto>>>> GetAllSensors()
        {
            try
            {
                var sensors = await _sensorService.GetAllSensorsAsync();
                return Ok(ApiResponse<List<SensorDto>>.SuccessResponse(sensors));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting all sensors");
                return StatusCode(500, ApiResponse<List<SensorDto>>.ErrorResponse(
                    "Đã xảy ra lỗi khi lấy danh sách sensors"
                ));
            }
        }

        /// <summary>
        /// Lấy danh sách sensors đang hoạt động
        /// </summary>
        [HttpGet("active")]
        public async Task<ActionResult<ApiResponse<List<SensorDto>>>> GetActiveSensors()
        {
            try
            {
                var sensors = await _sensorService.GetActiveSensorsAsync();
                return Ok(ApiResponse<List<SensorDto>>.SuccessResponse(sensors));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting active sensors");
                return StatusCode(500, ApiResponse<List<SensorDto>>.ErrorResponse(
                    "Đã xảy ra lỗi khi lấy danh sách sensors hoạt động"
                ));
            }
        }

        /// <summary>
        /// Cập nhật thông tin sensor
        /// </summary>
        [HttpPut("{id}")]
        public async Task<ActionResult<ApiResponse<SensorDto>>> UpdateSensor(
            int id,
            [FromBody] UpdateSensorDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(ApiResponse<SensorDto>.ErrorResponse(
                        "Dữ liệu không hợp lệ",
                        ModelState.Values.SelectMany(v => v.Errors.Select(e => e.ErrorMessage)).ToList()
                    ));
                }

                var sensor = await _sensorService.UpdateSensorAsync(id, dto);
                if (sensor == null)
                {
                    return NotFound(ApiResponse<SensorDto>.ErrorResponse("Không tìm thấy sensor"));
                }

                return Ok(ApiResponse<SensorDto>.SuccessResponse(sensor, "Cập nhật sensor thành công"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating sensor: {Id}", id);
                return StatusCode(500, ApiResponse<SensorDto>.ErrorResponse(
                    "Đã xảy ra lỗi khi cập nhật sensor"
                ));
            }
        }

        /// <summary>
        /// Xóa sensor
        /// </summary>
        [HttpDelete("{id}")]
        public async Task<ActionResult<ApiResponse<bool>>> DeleteSensor(int id)
        {
            try
            {
                var result = await _sensorService.DeleteSensorAsync(id);
                if (!result)
                {
                    return NotFound(ApiResponse<bool>.ErrorResponse("Không tìm thấy sensor"));
                }

                return Ok(ApiResponse<bool>.SuccessResponse(true, "Xóa sensor thành công"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting sensor: {Id}", id);
                return StatusCode(500, ApiResponse<bool>.ErrorResponse(
                    "Đã xảy ra lỗi khi xóa sensor"
                ));
            }
        }

        /// <summary>
        /// Bật/tắt trạng thái sensor
        /// </summary>
        [HttpPatch("{id}/toggle")]
        public async Task<ActionResult<ApiResponse<bool>>> ToggleSensorStatus(int id)
        {
            try
            {
                var result = await _sensorService.ToggleSensorStatusAsync(id);
                if (!result)
                {
                    return NotFound(ApiResponse<bool>.ErrorResponse("Không tìm thấy sensor"));
                }

                return Ok(ApiResponse<bool>.SuccessResponse(true, "Thay đổi trạng thái sensor thành công"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error toggling sensor status: {Id}", id);
                return StatusCode(500, ApiResponse<bool>.ErrorResponse(
                    "Đã xảy ra lỗi khi thay đổi trạng thái sensor"
                ));
            }
        }
    }
}
