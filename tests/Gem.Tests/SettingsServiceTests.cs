using System.IO;
using Gem.Models;
using Gem.Services;

namespace Gem.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GemTests_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void LoadAndSaveSettings_PersistsValues()
    {
        var service = new SettingsService(_tempDir);
        var settings = new AppSettings
        {
            DefaultDownloadPath = Path.Combine(_tempDir, "Downloads"),
            ThemeMode = AppThemeMode.Dark,
            ListenPort = 51413,
            EnableDht = false,
            MaxDownloadSpeedKBps = 2048,
            MaxUploadSpeedKBps = 512,
            MaxConnections = 220,
            ReannounceIntervalSeconds = 120,
            DefaultTrackers = ["udp://tracker.opentrackr.org:1337/announce", "https://tracker.example.com/announce"],
            AutoStartOnAdd = false,
            UpdateRepository = "testuser/Gem",
            AutoCheckUpdatesOnStartup = false
        };

        service.SaveSettings(settings);

        var reloaded = new SettingsService(_tempDir).LoadSettings();
        Assert.Equal(AppThemeMode.Dark, reloaded.ThemeMode);
        Assert.Equal(51413, reloaded.ListenPort);
        Assert.False(reloaded.EnableDht);
        Assert.Equal(2048, reloaded.MaxDownloadSpeedKBps);
        Assert.Equal(512, reloaded.MaxUploadSpeedKBps);
        Assert.Equal(220, reloaded.MaxConnections);
        Assert.Equal(120, reloaded.ReannounceIntervalSeconds);
        Assert.Equal(2, reloaded.DefaultTrackers.Count);
        Assert.False(reloaded.AutoStartOnAdd);
        Assert.Equal("testuser/Gem", reloaded.UpdateRepository);
        Assert.False(reloaded.AutoCheckUpdatesOnStartup);
    }

    [Fact]
    public void SaveAndLoadSession_PersistsEntries()
    {
        var service = new SettingsService(_tempDir);
        var entries = new List<SavedTorrentEntry>
        {
            new()
            {
                InfoHash = "AABBCCDDEEFF",
                Name = "Ubuntu 24.04",
                SavePath = @"D:\Downloads",
                MagnetUri = "magnet:?xt=urn:btih:AABBCCDDEEFF",
                WasPaused = true
            }
        };

        service.SaveSession(entries);
        var loaded = new SettingsService(_tempDir).LoadSession();

        Assert.Single(loaded);
        Assert.Equal("Ubuntu 24.04", loaded[0].Name);
        Assert.True(loaded[0].WasPaused);
    }

    [Fact]
    public void MigrateLegacyAppData_CopiesExistingStateOnceWithoutOverwritingNewData()
    {
        string legacyDir = Path.Combine(_tempDir, "Legacy");
        string targetDir = Path.Combine(_tempDir, "Gem");
        Directory.CreateDirectory(Path.Combine(legacyDir, "torrents"));
        Directory.CreateDirectory(Path.Combine(legacyDir, "cache"));

        File.WriteAllText(Path.Combine(legacyDir, "settings.json"), "{\"ListenPort\":55555}");
        File.WriteAllText(Path.Combine(legacyDir, "session.json"), "[{\"InfoHash\":\"ABC\"}]");
        File.WriteAllText(Path.Combine(legacyDir, "torrents", "ABC.torrent"), "torrent-bytes");

        SettingsService.MigrateLegacyAppData(legacyDir, targetDir);

        Assert.Equal("{\"ListenPort\":55555}", File.ReadAllText(Path.Combine(targetDir, "settings.json")));
        Assert.Equal("[{\"InfoHash\":\"ABC\"}]", File.ReadAllText(Path.Combine(targetDir, "session.json")));
        Assert.Equal("torrent-bytes", File.ReadAllText(Path.Combine(targetDir, "torrents", "ABC.torrent")));

        // Subsequent call must not overwrite modified target data
        File.WriteAllText(Path.Combine(targetDir, "settings.json"), "{\"ListenPort\":60000}");
        SettingsService.MigrateLegacyAppData(legacyDir, targetDir);
        Assert.Equal("{\"ListenPort\":60000}", File.ReadAllText(Path.Combine(targetDir, "settings.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }
}
