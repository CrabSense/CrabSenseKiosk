using CrabSenseKiosk.DTOs;

namespace CrabSenseKiosk.Services
{
    public interface ISensorService
    {
        Task<SensorDto> CreateSensorAsync(CreateSensorDto dto);
        Task<SensorDto?> GetSensorByIdAsync(int id);
        Task<List<SensorDto>> GetAllSensorsAsync();
        Task<List<SensorDto>> GetActiveSensorsAsync();
        Task<SensorDto?> UpdateSensorAsync(int id, UpdateSensorDto dto);
        Task<bool> DeleteSensorAsync(int id);
        Task<bool> ToggleSensorStatusAsync(int id);
    }
}
