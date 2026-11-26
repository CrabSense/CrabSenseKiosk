namespace CrabSenseKiosk.Services.WaterBench;

public sealed class WaterBenchOptions
{
    public const string SectionName = "WaterBench";
    public bool Enabled { get; set; }
    public bool EnableSupervisedControl { get; set; }
    public bool UseBackendSchedule { get; set; }
    public bool EnableBatchSync { get; set; }
    public bool EnableMediaSync { get; set; }
    public bool EnableNh3EvidenceSync { get; set; }
    public Guid BackendStationId { get; set; }
    public string MasterPort { get; set; } = "";
    public string SlavePort { get; set; } = "";
    public string MasterHardwareId { get; set; } = "";
    public string SlaveHardwareId { get; set; } = "";
    public string LocalOperatorKey { get; set; } = "";
    public string EvidenceDirectory { get; set; } = "water-bench-spool";
    public long MaxSpoolBytes { get; set; } = 256L * 1024 * 1024;
    public int MaxSpoolFiles { get; set; } = 20000;
}
