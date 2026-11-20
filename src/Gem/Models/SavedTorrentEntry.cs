namespace Gem.Models;

public sealed class SavedTorrentEntry
{
    public string InfoHash { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string SavePath { get; set; } = string.Empty;

    public string? TorrentFilePath { get; set; }

    public string? MagnetUri { get; set; }

    public bool WasPaused { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    public List<string> CustomTrackers { get; set; } = [];

    public bool? SeedAfterDownloadOverride { get; set; }

    public PostCompletionAction PostCompletionAction { get; set; }
}
