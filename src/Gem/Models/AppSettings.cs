namespace Gem.Models;

public enum AppThemeMode
{
    System,
    Dark,
    Light
}

public enum AppColorTheme
{
    Mint,
    Tomato,
    Purple,
    Sky,
    Cobalt,
    Cyan,
    Amber,
    Rose,
    Orchid,
    Forest,
    Gold,
    Slate,
    Indigo,
    Coral,
    Sakura,
    Mocha
}

public sealed class AppSettings
{
    public const string DefaultUpdateRepository = "kAdimiron/gem";

    public string DefaultDownloadPath { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Gem");

    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;

    public AppColorTheme ColorTheme { get; set; } = AppColorTheme.Mint;

    public string Language { get; set; } = string.Empty;

    public int ListenPort { get; set; } = 55123;

    public bool EnableDht { get; set; } = true;

    public bool EnableLocalPeerDiscovery { get; set; } = true;

    public bool EnablePortForwarding { get; set; } = true;

    /// <summary>0 means unlimited.</summary>
    public int MaxDownloadSpeedKBps { get; set; } = 0;

    /// <summary>0 means unlimited.</summary>
    public int MaxUploadSpeedKBps { get; set; } = 0;

    /// <summary>Maximum peer connections per torrent.</summary>
    public int MaxConnections { get; set; } = 100;

    public TorrentSortKind SortKind { get; set; } = TorrentSortKind.DateAddedDesc;

    /// <summary>0 means respect tracker's own update interval; &gt; 0 forces reannounce every N seconds.</summary>
    public int ReannounceIntervalSeconds { get; set; } = 0;

    public List<string> DefaultTrackers { get; set; } = [];

    public bool AutoStartOnAdd { get; set; } = true;

    public bool SeedAfterDownload { get; set; } = true;

    public bool ShowSpeedGraph { get; set; } = true;

    public bool MinimizeToTrayOnClose { get; set; } = true;

    public bool StartMinimized { get; set; } = false;

    public bool LaunchAtStartup { get; set; } = false;

    public string UpdateRepository { get; set; } = DefaultUpdateRepository;

    public bool AutoCheckUpdatesOnStartup { get; set; } = true;
}
