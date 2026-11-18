namespace Gem.Models;

public enum AppThemeMode
{
    System,
    Dark,
    Light
}

public sealed class AppSettings
{
    public string DefaultDownloadPath { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Gem");

    public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;

    public string Language { get; set; } = string.Empty;

    public int ListenPort { get; set; } = 55123;

    public bool EnableDht { get; set; } = true;

    public bool EnableLocalPeerDiscovery { get; set; } = true;

    public bool EnablePortForwarding { get; set; } = true;

    /// <summary>0 means unlimited.</summary>
    public int MaxDownloadSpeedKBps { get; set; } = 0;

    /// <summary>0 means unlimited.</summary>
    public int MaxUploadSpeedKBps { get; set; } = 0;

    public int MaxConnections { get; set; } = 300;

    /// <summary>0 means respect tracker's own update interval; &gt; 0 forces reannounce every N seconds.</summary>
    public int ReannounceIntervalSeconds { get; set; } = 0;

    public List<string> DefaultTrackers { get; set; } = [];

    public bool AutoStartOnAdd { get; set; } = true;

    public bool ShowSpeedGraph { get; set; } = true;

    public bool MinimizeToTrayOnClose { get; set; } = true;

    public string UpdateRepository { get; set; } = string.Empty;

    public bool AutoCheckUpdatesOnStartup { get; set; } = true;
}
