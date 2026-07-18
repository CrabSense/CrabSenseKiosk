using CrabSenseKiosk.DTOs;

namespace CrabSenseKiosk.Services
{
    public interface IWaterQualityService
    {
        Task<WaterQualityReadingDto> CreateReadingAsync(CreateWaterQualityReadingDto dto);
        Task<WaterQualityReadingDto?> GetReadingByIdAsync(int id);
        Task<PagedResponse<WaterQualityReadingDto>> GetReadingsAsync(WaterQualityQueryDto query);
        Task<WaterQualityStatisticsDto> GetStatisticsAsync(DateTime? startDate, DateTime? endDate, int? sensorId);
        Task<List<WaterQualityReadingDto>> GetLatestReadingsAsync(int count = 10);
        Task<bool> DeleteReadingAsync(int id);
    }
}
