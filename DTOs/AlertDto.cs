using System.ComponentModel.DataAnnotations;

namespace CrabSenseKiosk.DTOs
{
    public class AlertDto
    {
        public int Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string AlertType { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public double Value { get; set; }
        public double ThresholdMin { get; set; }
        public double ThresholdMax { get; set; }
        public int? SensorId { get; set; }
        public string? SensorName { get; set; }
        public int? ReadingId { get; set; }
        public bool IsAcknowledged { get; set; }
        public DateTime? AcknowledgedAt { get; set; }
        public string? AcknowledgedBy { get; set; }
        public string? Notes { get; set; }
    }

    public class AcknowledgeAlertDto
    {
        [Required(ErrorMessage = "Người xác nhận là bắt buộc")]
        [MaxLength(200, ErrorMessage = "Tên không được vượt quá 200 ký tự")]
        public string AcknowledgedBy { get; set; } = string.Empty;

        [MaxLength(1000, ErrorMessage = "Ghi chú không được vượt quá 1000 ký tự")]
        public string? Notes { get; set; }
    }

    public class AlertQueryDto
    {
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int? SensorId { get; set; }
        public string? AlertType { get; set; }
        public string? Severity { get; set; }
        public bool? OnlyUnacknowledged { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }
}
