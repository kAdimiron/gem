namespace Gem.Models;

public sealed class DailyTrafficPoint
{
    public string DateKey { get; set; } = string.Empty;

    public long DownloadedBytes { get; set; }

    public long UploadedBytes { get; set; }
}

public sealed class TrafficStatsData
{
    public long TotalDownloadedBytes { get; set; }

    public long TotalUploadedBytes { get; set; }

    public List<DailyTrafficPoint> Days { get; set; } = [];
}
