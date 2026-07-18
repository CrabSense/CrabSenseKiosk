namespace CrabSenseKiosk.Services
{
    public interface IAlertService
    {
        Task ProcessWaterQualityAlert(int readingId);
        Task<List<string>> GetActiveAlertsAsync();
    }
}
