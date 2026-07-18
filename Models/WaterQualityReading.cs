using System.ComponentModel.DataAnnotations;

namespace CrabSenseKiosk.Models
{
    public class WaterQualityReading
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        [Range(-10, 50)]
        public double Temperature { get; set; }

        [Range(0, 14)]
        public double pH { get; set; }

        [Range(0, 20)]
        public double DissolvedOxygen { get; set; }

        [Range(0, 50)]
        public double Salinity { get; set; }

        [Range(0, 1000)]
        public double? Turbidity { get; set; }

        [Range(0, 10)]
        public double? Ammonia { get; set; }

        public int? SensorId { get; set; }
        public Sensor? Sensor { get; set; }

        public string? Notes { get; set; }

        public bool IsAnomaly { get; set; } = false;
    }
}
