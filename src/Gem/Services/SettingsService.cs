using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gem.Models;

namespace Gem.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _appDataDir;
    private readonly string _settingsPath;
    private readonly string _sessionPath;
    private readonly string _trafficStatsPath;

    public string CacheDirectory { get; }
    public string TorrentsStoreDirectory { get; }

    public AppSettings Current { get; private set; } = new();

    public SettingsService(string? baseDirectory = null)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _appDataDir = baseDirectory ?? Path.Combine(localAppData, "Gem");

        if (baseDirectory is null)
        {
            // One-time migration from pre-rename app data folders so existing torrents and settings are preserved.
            MigrateLegacyAppData(Path.Combine(localAppData, "Lantern"), _appDataDir);
            MigrateLegacyAppData(Path.Combine(localAppData, "FluentTorrent"), _appDataDir);
        }

        _settingsPath = Path.Combine(_appDataDir, "settings.json");
        _sessionPath = Path.Combine(_appDataDir, "session.json");
        _trafficStatsPath = Path.Combine(_appDataDir, "traffic_stats.json");
        CacheDirectory = Path.Combine(_appDataDir, "cache");
        TorrentsStoreDirectory = Path.Combine(_appDataDir, "torrents");

        EnsureDirectories();
    }

    internal static void MigrateLegacyAppData(string legacyDir, string targetDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(legacyDir) ||
                string.IsNullOrWhiteSpace(targetDir) ||
                string.Equals(legacyDir, targetDir, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(legacyDir))
            {
                return;
            }

            if (File.Exists(Path.Combine(targetDir, "settings.json")) ||
                File.Exists(Path.Combine(targetDir, "session.json")))
            {
                return;
            }

            Directory.CreateDirectory(targetDir);
            foreach (string sourceSubDir in Directory.EnumerateDirectories(legacyDir, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(legacyDir, sourceSubDir);
                Directory.CreateDirectory(Path.Combine(targetDir, relative));
            }

            foreach (string sourceFile in Directory.EnumerateFiles(legacyDir, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(legacyDir, sourceFile);
                string destFile = Path.Combine(targetDir, relative);
                string? destParent = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destParent))
                    Directory.CreateDirectory(destParent);
                if (!File.Exists(destFile))
                    File.Copy(sourceFile, destFile, overwrite: false);
            }
        }
        catch
        {
            // Never block startup if legacy folder migration encounters a locked or transient file.
        }
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(_appDataDir);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(TorrentsStoreDirectory);
    }

    public AppSettings LoadSettings()
    {
        EnsureDirectories();
        if (!File.Exists(_settingsPath))
        {
            Current = new AppSettings
            {
                Language = LocalizationService.DetectSystemLanguageCode()
            };
            Directory.CreateDirectory(Current.DefaultDownloadPath);
            SaveSettings(Current);
            return Current;
        }

        try
        {
            string json = File.ReadAllText(_settingsPath);
            Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }

        if (string.IsNullOrWhiteSpace(Current.Language))
        {
            Current.Language = LocalizationService.DetectSystemLanguageCode();
            SaveSettings(Current);
        }
        else
        {
            Current.Language = LocalizationService.ResolveSupportedLanguageCode(Current.Language);
        }

        if (!string.IsNullOrWhiteSpace(Current.DefaultDownloadPath))
        {
            try { Directory.CreateDirectory(Current.DefaultDownloadPath); } catch { }
        }

        return Current;
    }

    public void SaveSettings(AppSettings settings)
    {
        EnsureDirectories();
        Current = settings;
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }

    public List<SavedTorrentEntry> LoadSession()
    {
        EnsureDirectories();
        if (!File.Exists(_sessionPath))
            return [];

        try
        {
            string json = File.ReadAllText(_sessionPath);
            return JsonSerializer.Deserialize<List<SavedTorrentEntry>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void SaveSession(IEnumerable<SavedTorrentEntry> entries)
    {
        EnsureDirectories();
        string json = JsonSerializer.Serialize(entries.ToList(), JsonOptions);
        File.WriteAllText(_sessionPath, json);
    }

    public TrafficStatsData LoadTrafficStats()
    {
        EnsureDirectories();
        if (!File.Exists(_trafficStatsPath))
            return new TrafficStatsData();

        try
        {
            string json = File.ReadAllText(_trafficStatsPath);
            return JsonSerializer.Deserialize<TrafficStatsData>(json, JsonOptions) ?? new TrafficStatsData();
        }
        catch
        {
            return new TrafficStatsData();
        }
    }

    public void SaveTrafficStats(TrafficStatsData data)
    {
        EnsureDirectories();
        string json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(_trafficStatsPath, json);
    }

    public string PersistTorrentFile(string sourcePath, string infoHashHex)
    {
        EnsureDirectories();
        string safeHash = infoHashHex.Replace(":", "_").ToLowerInvariant();
        string destPath = Path.Combine(TorrentsStoreDirectory, $"{safeHash}.torrent");
        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourcePath, destPath, overwrite: true);
        }
        return destPath;
    }
}
