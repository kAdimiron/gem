namespace Gem.Models;

public enum StoreNavSection
{
    Home,
    AddTorrent,
    AddMagnet,
    Downloads,
    Active,
    Seeding,
    Library,
    CreateTorrent,
    Settings
}

public enum TorrentStatusKind
{
    Downloading,
    Seeding,
    Paused,
    Stopped,
    Checking,
    Metadata,
    Error
}

public enum TorrentFilterKind
{
    All,
    Downloading,
    Paused,
    Completed,
    Errors
}

public enum TorrentFilePriority
{
    DoNotDownload = 0,
    Low = 1,
    Normal = 4,
    High = 7
}
