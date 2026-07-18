using System.ComponentModel.DataAnnotations;

namespace CrabSenseKiosk.DTOs
{
    public class WaterQualityReadingDto
    {
        public int Id { get; set; }
        public DateTime Timestamp { get; set; }
        public double Temperature { get; set; }
        public double pH { get; set; }
        public double DissolvedOxygen { get; set; }
        public double Salinity { get; set; }
        public double? Turbidity { get; set; }
        public double? Ammonia { get; set; }
        public int? SensorId { get; set; }
        public string? SensorName { get; set; }
        public string? Notes { get; set; }
        public bool IsAnomaly { get; set; }
    }

    public class CreateWaterQualityReadingDto
    {
        [Range(-10, 50, ErrorMessage = "Nhiệt độ phải từ -10 đến 50°C")]
        public double Temperature { get; set; }

        [Range(0, 14, ErrorMessage = "pH phải từ 0 đến 14")]
        public double pH { get; set; }

        [Range(0, 20, ErrorMessage = "Oxy hòa tan phải từ 0 đến 20 mg/L")]
        public double DissolvedOxygen { get; set; }

        [Range(0, 50, ErrorMessage = "Độ mặn phải từ 0 đến 50 ppt")]
        public double Salinity { get; set; }

        [Range(0, 1000, ErrorMessage = "Độ đục phải từ 0 đến 1000 NTU")]
        public double? Turbidity { get; set; }

        [Range(0, 10, ErrorMessage = "Ammonia phải từ 0 đến 10 mg/L")]
        public double? Ammonia { get; set; }

        public int? SensorId { get; set; }

        [MaxLength(500, ErrorMessage = "Ghi chú không được vượt quá 500 ký tự")]
        public string? Notes { get; set; }
    }

    public class WaterQualityStatisticsDto
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public int TotalReadings { get; set; }
        public int AnomalyCount { get; set; }
        public double AnomalyPercentage { get; set; }
        
        public ParameterStats Temperature { get; set; } = new();
        public ParameterStats pH { get; set; } = new();
        public ParameterStats DissolvedOxygen { get; set; } = new();
        public ParameterStats Salinity { get; set; } = new();
    }

    public class ParameterStats
    {
        public double Min { get; set; }
        public double Max { get; set; }
        public double Average { get; set; }
        public double Current { get; set; }
        public string Status { get; set; } = "Normal";
    }

    public class WaterQualityQueryDto
    {
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int? SensorId { get; set; }
        public bool? OnlyAnomalies { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }
}
