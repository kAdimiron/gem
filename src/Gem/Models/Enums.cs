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

public enum TorrentSortKind
{
    DateAddedDesc,
    DateAddedAsc,
    NameAsc,
    NameDesc,
    SizeDesc,
    SizeAsc,
    ProgressDesc,
    DownloadSpeedDesc,
    UploadSpeedDesc
}

public enum TorrentFilePriority
{
    DoNotDownload = 0,
    Low = 1,
    Normal = 4,
    High = 7
}

public enum PostCompletionAction
{
    None = 0,
    Sleep = 1,
    Hibernate = 2,
    Shutdown = 3
}
