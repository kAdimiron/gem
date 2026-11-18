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
    public void LoadSettings_DefaultsUpdateRepositoryToKAdimironGem()
    {
        var service = new SettingsService(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "settings.json"), "{\"UpdateRepository\":\"\"}");

        var loaded = service.LoadSettings();

        Assert.Equal("kAdimiron/gem", AppSettings.DefaultUpdateRepository);
        Assert.Equal("kAdimiron/gem", loaded.UpdateRepository);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }
}
