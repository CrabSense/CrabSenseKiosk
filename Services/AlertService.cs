using CrabSenseKiosk.Data;
using Microsoft.EntityFrameworkCore;

namespace CrabSenseKiosk.Services
{
    public class AlertService : IAlertService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<AlertService> _logger;
        private readonly IConfiguration _configuration;

        public AlertService(
            ApplicationDbContext context,
            ILogger<AlertService> logger,
            IConfiguration configuration)
        {
            _context = context;
            _logger = logger;
            _configuration = configuration;
        }

        public async Task ProcessWaterQualityAlert(int readingId)
        {
            var reading = await _context.WaterQualityReadings
                .Include(r => r.Sensor)
                .FirstOrDefaultAsync(r => r.Id == readingId);

            if (reading == null || !reading.IsAnomaly)
                return;

            var alerts = new List<string>();
            var safeRanges = _configuration.GetSection("WaterQuality:SafeRanges");

            // Check temperature
            var tempMin = safeRanges.GetValue<double>("Temperature:Min");
            var tempMax = safeRanges.GetValue<double>("Temperature:Max");
            if (reading.Temperature < tempMin || reading.Temperature > tempMax)
            {
                alerts.Add($"CẢNH BÁO: Nhiệt độ {reading.Temperature}°C nằm ngoài phạm vi an toàn ({tempMin}-{tempMax}°C)");
            }

            // Check pH
            var phMin = safeRanges.GetValue<double>("pH:Min");
            var phMax = safeRanges.GetValue<double>("pH:Max");
            if (reading.pH < phMin || reading.pH > phMax)
            {
                alerts.Add($"CẢNH BÁO: pH {reading.pH} nằm ngoài phạm vi an toàn ({phMin}-{phMax})");
            }

            // Check dissolved oxygen
            var doMin = safeRanges.GetValue<double>("DissolvedOxygen:Min");
            var doMax = safeRanges.GetValue<double>("DissolvedOxygen:Max");
            if (reading.DissolvedOxygen < doMin || reading.DissolvedOxygen > doMax)
            {
                alerts.Add($"CẢNH BÁO: Oxy hòa tan {reading.DissolvedOxygen} mg/L nằm ngoài phạm vi an toàn ({doMin}-{doMax} mg/L)");
            }

            // Check salinity
            var salinityMin = safeRanges.GetValue<double>("Salinity:Min");
            var salinityMax = safeRanges.GetValue<double>("Salinity:Max");
            if (reading.Salinity < salinityMin || reading.Salinity > salinityMax)
            {
                alerts.Add($"CẢNH BÁO: Độ mặn {reading.Salinity} ppt nằm ngoài phạm vi an toàn ({salinityMin}-{salinityMax} ppt)");
            }

            foreach (var alert in alerts)
            {
                _logger.LogWarning($"[Alert] Sensor: {reading.Sensor?.Name ?? "Unknown"} - {alert}");
            }
        }

        public async Task<List<string>> GetActiveAlertsAsync()
        {
            var recentAnomalies = await _context.WaterQualityReadings
                .Include(r => r.Sensor)
                .Where(r => r.IsAnomaly && r.Timestamp >= DateTime.UtcNow.AddHours(-24))
                .OrderByDescending(r => r.Timestamp)
                .Take(10)
                .ToListAsync();

            var alerts = new List<string>();
            foreach (var reading in recentAnomalies)
            {
                alerts.Add($"{reading.Timestamp:dd/MM/yyyy HH:mm} - {reading.Sensor?.Name ?? "Unknown"}: Phát hiện bất thường");
            }

            return alerts;
        }
    }
}
