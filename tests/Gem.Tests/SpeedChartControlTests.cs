using System.IO;
using Gem.Models;
using Gem.Services;
using Gem.ViewModels;

namespace Gem.Tests;

public sealed class SpeedChartControlTests : IDisposable
{
    private readonly string _tempDir;

    public SpeedChartControlTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Gem_ChartTrayTests_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task MinimizeToTrayOnClose_DefaultsToTrue_AndPersistsChanges()
    {
        var settings = new SettingsService(_tempDir);
        Assert.True(settings.LoadSettings().MinimizeToTrayOnClose);

        // Legacy settings.json without MinimizeToTrayOnClose should also default to true
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "settings.json"), "{\"ListenPort\": 55123}");
        Assert.True(settings.LoadSettings().MinimizeToTrayOnClose);

        var vm = new MainViewModel(new FakeEngine(), settings, new ThemeService());
        await vm.InitializeAsync();
        Assert.True(vm.SettingsMinimizeToTrayOnClose);
        Assert.True(vm.ShouldMinimizeToTrayOnClose);

        vm.SettingsMinimizeToTrayOnClose = false;
        Assert.False(vm.ShouldMinimizeToTrayOnClose);
        Assert.False(settings.LoadSettings().MinimizeToTrayOnClose);
    }

    private sealed class FakeEngine : ITorrentEngineService
    {
        public System.Collections.ObjectModel.ObservableCollection<TorrentItemModel> Torrents { get; } = [];
        public long TotalDownloadSpeed => 0;
        public long TotalUploadSpeed => 0;
        public long AllTimeDownloadedBytes => 0;
        public long AllTimeUploadedBytes => 0;
        public IReadOnlyList<DailyTrafficPoint> DailyTrafficHistory => [];
        public int DhtNodesCount => 0;
        public bool IsRunning => true;
        public event Action? StatsUpdated { add { } remove { } }
        public event Action<int>? ListenPortChanged { add { } remove { } }
        public Task InitializeAsync() => Task.CompletedTask;
        public Task<TorrentItemModel> AddTorrentFileAsync(string torrentFilePath, string? savePath = null, bool? startImmediately = null) => throw new NotSupportedException();
        public Task<TorrentItemModel> AddMagnetAsync(string magnetUri, string? savePath = null, bool? startImmediately = null) => throw new NotSupportedException();
        public Task StartAsync(TorrentItemModel item) => Task.CompletedTask;
        public Task PauseAsync(TorrentItemModel item) => Task.CompletedTask;
        public Task StopAsync(TorrentItemModel item) => Task.CompletedTask;
        public Task ForceRecheckAsync(TorrentItemModel item) => Task.CompletedTask;
        public Task ReannounceAsync(TorrentItemModel item) => Task.CompletedTask;
        public Task RemoveAsync(TorrentItemModel item, bool deleteFiles) => Task.CompletedTask;
        public Task SetFilePriorityAsync(TorrentItemModel item, TorrentFileModel file, TorrentFilePriority priority) => Task.CompletedTask;
        public Task SetAllFilesPriorityAsync(TorrentItemModel item, TorrentFilePriority priority) => Task.CompletedTask;
        public Task AddTrackerAsync(TorrentItemModel item, string trackerUrl) => Task.CompletedTask;
        public Task RemoveTrackerAsync(TorrentItemModel item, string trackerUri) => Task.CompletedTask;
        public Task ApplySettingsAsync(AppSettings settings) => Task.CompletedTask;
        public Task RefreshDetailsAsync(TorrentItemModel item) => Task.CompletedTask;
        public Task ShutdownAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
