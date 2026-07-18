using System.ComponentModel.DataAnnotations;

namespace CrabSenseKiosk.Models
{
    public class Alert
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        [Required]
        [MaxLength(50)]
        public string AlertType { get; set; } = string.Empty; // Temperature, pH, DO, Salinity

        [Required]
        [MaxLength(20)]
        public string Severity { get; set; } = "Medium"; // Low, Medium, High, Critical

        [Required]
        [MaxLength(500)]
        public string Message { get; set; } = string.Empty;

        public double Value { get; set; }

        public double ThresholdMin { get; set; }

        public double ThresholdMax { get; set; }

        public int? SensorId { get; set; }
        public Sensor? Sensor { get; set; }

        public int? ReadingId { get; set; }
        public WaterQualityReading? Reading { get; set; }

        public bool IsAcknowledged { get; set; } = false;

        public DateTime? AcknowledgedAt { get; set; }

        [MaxLength(200)]
        public string? AcknowledgedBy { get; set; }

        [MaxLength(1000)]
        public string? Notes { get; set; }
    }
}
