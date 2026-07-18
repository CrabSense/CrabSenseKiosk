using System.ComponentModel.DataAnnotations;

namespace CrabSenseKiosk.DTOs
{
    public class SensorDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string SensorType { get; set; } = string.Empty;
        public string? Location { get; set; }
        public bool IsActive { get; set; }
        public DateTime InstallationDate { get; set; }
        public DateTime? LastMaintenanceDate { get; set; }
        public DateTime? NextMaintenanceDate { get; set; }
        public string? Description { get; set; }
    }

    public class CreateSensorDto
    {
        [Required(ErrorMessage = "Tên sensor là bắt buộc")]
        [MaxLength(100, ErrorMessage = "Tên không được vượt quá 100 ký tự")]
        public string Name { get; set; } = string.Empty;

        [Required(ErrorMessage = "Loại sensor là bắt buộc")]
        [MaxLength(50, ErrorMessage = "Loại sensor không được vượt quá 50 ký tự")]
        public string SensorType { get; set; } = string.Empty;

        [MaxLength(200, ErrorMessage = "Vị trí không được vượt quá 200 ký tự")]
        public string? Location { get; set; }

        public bool IsActive { get; set; } = true;

        [MaxLength(500, ErrorMessage = "Mô tả không được vượt quá 500 ký tự")]
        public string? Description { get; set; }
    }

    public class UpdateSensorDto
    {
        [Required(ErrorMessage = "Tên sensor là bắt buộc")]
        [MaxLength(100, ErrorMessage = "Tên không được vượt quá 100 ký tự")]
        public string Name { get; set; } = string.Empty;

        [Required(ErrorMessage = "Loại sensor là bắt buộc")]
        [MaxLength(50, ErrorMessage = "Loại sensor không được vượt quá 50 ký tự")]
        public string SensorType { get; set; } = string.Empty;

        [MaxLength(200, ErrorMessage = "Vị trí không được vượt quá 200 ký tự")]
        public string? Location { get; set; }

        public bool IsActive { get; set; }

        public DateTime? LastMaintenanceDate { get; set; }

        public DateTime? NextMaintenanceDate { get; set; }

        [MaxLength(500, ErrorMessage = "Mô tả không được vượt quá 500 ký tự")]
        public string? Description { get; set; }
    }
}
