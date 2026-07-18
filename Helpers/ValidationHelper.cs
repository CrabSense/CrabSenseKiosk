namespace CrabSenseKiosk.Helpers
{
    public static class ValidationHelper
    {
        /// <summary>
        /// Kiểm tra giá trị có nằm trong khoảng cho phép
        /// </summary>
        public static bool IsInRange(double value, double min, double max)
        {
            return value >= min && value <= max;
        }

        /// <summary>
        /// Kiểm tra giá trị nhiệt độ
        /// </summary>
        public static bool IsValidTemperature(double temperature)
        {
            return IsInRange(temperature, -10, 50);
        }

        /// <summary>
        /// Kiểm tra giá trị pH
        /// </summary>
        public static bool IsValidPH(double pH)
        {
            return IsInRange(pH, 0, 14);
        }

        /// <summary>
        /// Kiểm tra giá trị oxy hòa tan
        /// </summary>
        public static bool IsValidDissolvedOxygen(double dissolvedOxygen)
        {
            return IsInRange(dissolvedOxygen, 0, 20);
        }

        /// <summary>
        /// Kiểm tra giá trị độ mặn
        /// </summary>
        public static bool IsValidSalinity(double salinity)
        {
            return IsInRange(salinity, 0, 50);
        }

        /// <summary>
        /// Lấy trạng thái dựa trên giá trị và ngưỡng
        /// </summary>
        public static string GetStatus(double value, double min, double max)
        {
            if (value < min)
                return "Thấp";
            if (value > max)
                return "Cao";
            return "Bình thường";
        }
    }
}
