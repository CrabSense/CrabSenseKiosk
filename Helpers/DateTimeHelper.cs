namespace CrabSenseKiosk.Helpers
{
    public static class DateTimeHelper
    {
        /// <summary>
        /// Chuyển UTC sang múi giờ Việt Nam (UTC+7)
        /// </summary>
        public static DateTime ToVietnamTime(this DateTime utcDateTime)
        {
            var vietnamTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
            return TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, vietnamTimeZone);
        }

        /// <summary>
        /// Lấy thời gian bắt đầu ngày (00:00:00)
        /// </summary>
        public static DateTime StartOfDay(this DateTime date)
        {
            return date.Date;
        }

        /// <summary>
        /// Lấy thời gian kết thúc ngày (23:59:59)
        /// </summary>
        public static DateTime EndOfDay(this DateTime date)
        {
            return date.Date.AddDays(1).AddTicks(-1);
        }

        /// <summary>
        /// Lấy thời gian bắt đầu tuần
        /// </summary>
        public static DateTime StartOfWeek(this DateTime date, DayOfWeek startOfWeek = DayOfWeek.Monday)
        {
            int diff = (7 + (date.DayOfWeek - startOfWeek)) % 7;
            return date.AddDays(-1 * diff).Date;
        }

        /// <summary>
        /// Lấy thời gian bắt đầu tháng
        /// </summary>
        public static DateTime StartOfMonth(this DateTime date)
        {
            return new DateTime(date.Year, date.Month, 1);
        }

        /// <summary>
        /// Lấy thời gian kết thúc tháng
        /// </summary>
        public static DateTime EndOfMonth(this DateTime date)
        {
            return date.StartOfMonth().AddMonths(1).AddTicks(-1);
        }
    }
}
