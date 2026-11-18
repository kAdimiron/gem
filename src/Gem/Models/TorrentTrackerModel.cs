using Gem.Services;

namespace Gem.Models;

public sealed class TorrentTrackerModel
{
    public string Uri { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string StatusLabel { get; init; } = LocalizationService.Instance["TrackerUnknown"];

    public bool IsHealthy { get; init; }

    public bool IsError { get; init; }

    public int TierNumber { get; init; } = 1;

    public string TierDisplay => LocalizationService.Instance.Format("DetailsTrackerTierFormat", TierNumber);

    public int Seeders { get; init; }

    public int Leechers { get; init; }

    public int Downloaded { get; init; }

    public string DetailsLine { get; init; } = string.Empty;

    public string FailureMessage { get; init; } = string.Empty;
}
