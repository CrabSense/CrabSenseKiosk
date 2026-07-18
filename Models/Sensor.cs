using System.ComponentModel.DataAnnotations;

namespace CrabSenseKiosk.Models
{
    public class Sensor
    {
        [Key]
        public int Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [MaxLength(50)]
        public string SensorType { get; set; } = string.Empty;

        [MaxLength(200)]
        public string? Location { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime InstallationDate { get; set; } = DateTime.UtcNow;

        public DateTime? LastMaintenanceDate { get; set; }

        public DateTime? NextMaintenanceDate { get; set; }

        [MaxLength(500)]
        public string? Description { get; set; }

        // Navigation property
        public ICollection<WaterQualityReading>? Readings { get; set; }
    }
}
