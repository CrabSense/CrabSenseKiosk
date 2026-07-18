using CrabSenseKiosk.Data;
using CrabSenseKiosk.DTOs;
using CrabSenseKiosk.Models;
using Microsoft.EntityFrameworkCore;

namespace CrabSenseKiosk.Services
{
    public class WaterQualityService : IWaterQualityService
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly ILogger<WaterQualityService> _logger;

        public WaterQualityService(
            ApplicationDbContext context,
            IConfiguration configuration,
            ILogger<WaterQualityService> logger)
        {
            _context = context;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<WaterQualityReadingDto> CreateReadingAsync(CreateWaterQualityReadingDto dto)
        {
            try
            {
                var reading = new WaterQualityReading
                {
                    Timestamp = DateTime.UtcNow,
                    Temperature = dto.Temperature,
                    pH = dto.pH,
                    DissolvedOxygen = dto.DissolvedOxygen,
                    Salinity = dto.Salinity,
                    Turbidity = dto.Turbidity,
                    Ammonia = dto.Ammonia,
                    SensorId = dto.SensorId,
                    Notes = dto.Notes,
                    IsAnomaly = CheckForAnomalies(dto)
                };

                _context.WaterQualityReadings.Add(reading);
                await _context.SaveChangesAsync();

                return await MapToDto(reading);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating water quality reading");
                throw;
            }
        }

        public async Task<WaterQualityReadingDto?> GetReadingByIdAsync(int id)
        {
            var reading = await _context.WaterQualityReadings
                .Include(r => r.Sensor)
                .FirstOrDefaultAsync(r => r.Id == id);

            if (reading == null)
                return null;

            return await MapToDto(reading);
        }

        public async Task<PagedResponse<WaterQualityReadingDto>> GetReadingsAsync(WaterQualityQueryDto query)
        {
            var queryable = _context.WaterQualityReadings
                .Include(r => r.Sensor)
                .AsQueryable();

            // Apply filters
            if (query.StartDate.HasValue)
                queryable = queryable.Where(r => r.Timestamp >= query.StartDate.Value);

            if (query.EndDate.HasValue)
                queryable = queryable.Where(r => r.Timestamp <= query.EndDate.Value);

            if (query.SensorId.HasValue)
                queryable = queryable.Where(r => r.SensorId == query.SensorId.Value);

            if (query.OnlyAnomalies.HasValue && query.OnlyAnomalies.Value)
                queryable = queryable.Where(r => r.IsAnomaly);

            var totalRecords = await queryable.CountAsync();

            // Apply pagination
            var readings = await queryable
                .OrderByDescending(r => r.Timestamp)
                .Skip((query.PageNumber - 1) * query.PageSize)
                .Take(query.PageSize)
                .ToListAsync();

            var dtos = new List<WaterQualityReadingDto>();
            foreach (var reading in readings)
            {
                dtos.Add(await MapToDto(reading));
            }

            return new PagedResponse<WaterQualityReadingDto>
            {
                Data = dtos,
                PageNumber = query.PageNumber,
                PageSize = query.PageSize,
                TotalRecords = totalRecords,
                TotalPages = (int)Math.Ceiling(totalRecords / (double)query.PageSize)
            };
        }

        public async Task<WaterQualityStatisticsDto> GetStatisticsAsync(DateTime? startDate, DateTime? endDate, int? sensorId)
        {
            var queryable = _context.WaterQualityReadings.AsQueryable();

            var start = startDate ?? DateTime.UtcNow.AddDays(-7);
            var end = endDate ?? DateTime.UtcNow;

            queryable = queryable.Where(r => r.Timestamp >= start && r.Timestamp <= end);

            if (sensorId.HasValue)
                queryable = queryable.Where(r => r.SensorId == sensorId.Value);

            var readings = await queryable.ToListAsync();

            if (!readings.Any())
            {
                return new WaterQualityStatisticsDto
                {
                    StartDate = start,
                    EndDate = end
                };
            }

            var latest = readings.OrderByDescending(r => r.Timestamp).First();

            return new WaterQualityStatisticsDto
            {
                StartDate = start,
                EndDate = end,
                TotalReadings = readings.Count,
                AnomalyCount = readings.Count(r => r.IsAnomaly),
                AnomalyPercentage = readings.Count > 0 ? (double)readings.Count(r => r.IsAnomaly) / readings.Count * 100 : 0,
                Temperature = new ParameterStats
                {
                    Min = readings.Min(r => r.Temperature),
                    Max = readings.Max(r => r.Temperature),
                    Average = readings.Average(r => r.Temperature),
                    Current = latest.Temperature,
                    Status = GetParameterStatus("Temperature", latest.Temperature)
                },
                pH = new ParameterStats
                {
                    Min = readings.Min(r => r.pH),
                    Max = readings.Max(r => r.pH),
                    Average = readings.Average(r => r.pH),
                    Current = latest.pH,
                    Status = GetParameterStatus("pH", latest.pH)
                },
                DissolvedOxygen = new ParameterStats
                {
                    Min = readings.Min(r => r.DissolvedOxygen),
                    Max = readings.Max(r => r.DissolvedOxygen),
                    Average = readings.Average(r => r.DissolvedOxygen),
                    Current = latest.DissolvedOxygen,
                    Status = GetParameterStatus("DissolvedOxygen", latest.DissolvedOxygen)
                },
                Salinity = new ParameterStats
                {
                    Min = readings.Min(r => r.Salinity),
                    Max = readings.Max(r => r.Salinity),
                    Average = readings.Average(r => r.Salinity),
                    Current = latest.Salinity,
                    Status = GetParameterStatus("Salinity", latest.Salinity)
                }
            };
        }

        public async Task<List<WaterQualityReadingDto>> GetLatestReadingsAsync(int count = 10)
        {
            var readings = await _context.WaterQualityReadings
                .Include(r => r.Sensor)
                .OrderByDescending(r => r.Timestamp)
                .Take(count)
                .ToListAsync();

            var dtos = new List<WaterQualityReadingDto>();
            foreach (var reading in readings)
            {
                dtos.Add(await MapToDto(reading));
            }

            return dtos;
        }

        public async Task<bool> DeleteReadingAsync(int id)
        {
            var reading = await _context.WaterQualityReadings.FindAsync(id);
            if (reading == null)
                return false;

            _context.WaterQualityReadings.Remove(reading);
            await _context.SaveChangesAsync();
            return true;
        }

        private bool CheckForAnomalies(CreateWaterQualityReadingDto dto)
        {
            var safeRanges = _configuration.GetSection("WaterQuality:SafeRanges");

            var tempMin = safeRanges.GetValue<double>("Temperature:Min");
            var tempMax = safeRanges.GetValue<double>("Temperature:Max");
            if (dto.Temperature < tempMin || dto.Temperature > tempMax)
                return true;

            var phMin = safeRanges.GetValue<double>("pH:Min");
            var phMax = safeRanges.GetValue<double>("pH:Max");
            if (dto.pH < phMin || dto.pH > phMax)
                return true;

            var doMin = safeRanges.GetValue<double>("DissolvedOxygen:Min");
            var doMax = safeRanges.GetValue<double>("DissolvedOxygen:Max");
            if (dto.DissolvedOxygen < doMin || dto.DissolvedOxygen > doMax)
                return true;

            var salinityMin = safeRanges.GetValue<double>("Salinity:Min");
            var salinityMax = safeRanges.GetValue<double>("Salinity:Max");
            if (dto.Salinity < salinityMin || dto.Salinity > salinityMax)
                return true;

            return false;
        }

        private string GetParameterStatus(string parameter, double value)
        {
            var safeRanges = _configuration.GetSection("WaterQuality:SafeRanges");
            var min = safeRanges.GetValue<double>($"{parameter}:Min");
            var max = safeRanges.GetValue<double>($"{parameter}:Max");

            if (value < min || value > max)
                return "Cảnh báo";

            return "Bình thường";
        }

        private async Task<WaterQualityReadingDto> MapToDto(WaterQualityReading reading)
        {
            return new WaterQualityReadingDto
            {
                Id = reading.Id,
                Timestamp = reading.Timestamp,
                Temperature = reading.Temperature,
                pH = reading.pH,
                DissolvedOxygen = reading.DissolvedOxygen,
                Salinity = reading.Salinity,
                Turbidity = reading.Turbidity,
                Ammonia = reading.Ammonia,
                SensorId = reading.SensorId,
                SensorName = reading.Sensor?.Name,
                Notes = reading.Notes,
                IsAnomaly = reading.IsAnomaly
            };
        }
    }
}
