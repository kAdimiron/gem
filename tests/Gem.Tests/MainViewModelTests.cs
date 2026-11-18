using System.Collections.ObjectModel;
using System.IO;
using Gem.Helpers;
using Gem.Models;
using Gem.Services;
using Gem.ViewModels;

namespace Gem.Tests;

[Collection("Localization")]
public sealed class MainViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public MainViewModelTests()
    {
        LocalizationService.Instance.SetLanguage("ru-RU");
        _tempDir = Path.Combine(Path.GetTempPath(), "GemVmTests_" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task NavigationAndFiltering_UpdatesStoreRailAndFilteredTorrents()
    {
        var fakeEngine = new FakeTorrentEngineService();
        fakeEngine.Torrents.Add(new TorrentItemModel
        {
            InfoHash = "1",
            Name = "Ubuntu Desktop",
            StatusKind = TorrentStatusKind.Downloading,
            Progress = 42
        });
        fakeEngine.Torrents.Add(new TorrentItemModel
        {
            InfoHash = "2",
            Name = "Fedora Workstation",
            StatusKind = TorrentStatusKind.Seeding,
            Progress = 100
        });
        fakeEngine.Torrents.Add(new TorrentItemModel
        {
            InfoHash = "3",
            Name = "Arch Linux",
            StatusKind = TorrentStatusKind.Paused,
            Progress = 10
        });

        var settings = new SettingsService(_tempDir);
        var vm = new MainViewModel(fakeEngine, settings, new ThemeService());
        await vm.InitializeAsync();

        // Check Store navigation selection
        vm.NavigateToCommand.Execute(StoreNavSection.Seeding);
        Assert.Equal(StoreNavSection.Seeding, vm.CurrentSection);
        Assert.Single(vm.FilteredTorrents);
        Assert.Equal("Fedora Workstation", vm.FilteredTorrents[0].Name);
        Assert.True(vm.TopNavItems.Single(i => i.Section == StoreNavSection.Seeding).IsSelected);

        // Check badge counts
        Assert.Equal(1, vm.TopNavItems.Single(i => i.Section == StoreNavSection.Downloads).BadgeCount);
        Assert.Equal(2, vm.TopNavItems.Single(i => i.Section == StoreNavSection.Active).BadgeCount);

        // Check Downloads excludes completed/seeding torrents and respects search query
        vm.NavigateToCommand.Execute(StoreNavSection.Downloads);
        Assert.Equal(2, vm.FilteredTorrents.Count);
        Assert.DoesNotContain(vm.FilteredTorrents, t => t.Name == "Fedora Workstation");

        vm.SearchQuery = "Ubuntu";
        Assert.Single(vm.FilteredTorrents);
        Assert.Equal("Ubuntu Desktop", vm.FilteredTorrents[0].Name);

        // Check Active includes both downloading and seeding
        vm.SearchQuery = string.Empty;
        vm.NavigateToCommand.Execute(StoreNavSection.Active);
        Assert.Equal(2, vm.FilteredTorrents.Count);

        // Check Settings pinned item selection
        vm.NavigateToCommand.Execute(StoreNavSection.Settings);
        Assert.True(vm.SettingsNavItem.IsSelected);
        Assert.True(vm.IsSettingsView);
    }

    [Fact]
    public async Task LibrarySection_IncludesPausedCompletedTorrents()
    {
        var fakeEngine = new FakeTorrentEngineService();
        fakeEngine.Torrents.Add(new TorrentItemModel
        {
            InfoHash = "1",
            Name = "Completed Paused ISO",
            StatusKind = TorrentStatusKind.Paused,
            Progress = 100
        });
        fakeEngine.Torrents.Add(new TorrentItemModel
        {
            InfoHash = "2",
            Name = "Incomplete Paused ISO",
            StatusKind = TorrentStatusKind.Paused,
            Progress = 45
        });

        var vm = new MainViewModel(fakeEngine, new SettingsService(_tempDir), new ThemeService());
        await vm.InitializeAsync();

        vm.NavigateToCommand.Execute(StoreNavSection.Library);
        Assert.True(vm.IsLibraryView);
        Assert.True(vm.IsTorrentsView);
        Assert.Equal("Библиотека", vm.CurrentSectionTitle);
        Assert.Single(vm.FilteredTorrents);
        Assert.Equal("Completed Paused ISO", vm.FilteredTorrents[0].Name);
    }

    [Fact]
    public async Task SubmitSearchOrMagnet_AddsMagnetAndNavigatesToDownloads()
    {
        var fakeEngine = new FakeTorrentEngineService();
        var settings = new SettingsService(_tempDir);
        var vm = new MainViewModel(fakeEngine, settings, new ThemeService());
        await vm.InitializeAsync();

        vm.SearchQuery = "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567&dn=Debian";
        await vm.SubmitSearchOrMagnetCommand.ExecuteAsync(null);

        Assert.Single(fakeEngine.Torrents);
        Assert.Equal(StoreNavSection.Downloads, vm.CurrentSection);
        Assert.Equal(string.Empty, vm.SearchQuery);
    }

    [Fact]
    public void ThemeToggleIcon_ShowsSunInDarkModeAndMoonInLightMode()
    {
        var vm = new MainViewModel(new FakeTorrentEngineService(), new SettingsService(_tempDir), new ThemeService());
        vm.IsDarkTheme = true;
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.WeatherSunny24, vm.ThemeToggleIcon);

        vm.IsDarkTheme = false;
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.WeatherMoon24, vm.ThemeToggleIcon);
    }

    [Fact]
    public async Task OpenAndCloseTorrentDetails_UpdatesSelectionAndState()
    {
        var fakeEngine = new FakeTorrentEngineService();
        var torrent = new TorrentItemModel { InfoHash = "XYZ", Name = "Debian ISO" };
        fakeEngine.Torrents.Add(torrent);

        var vm = new MainViewModel(fakeEngine, new SettingsService(_tempDir), new ThemeService());
        await vm.InitializeAsync();

        vm.OpenTorrentDetailsCommand.Execute(torrent);
        Assert.True(vm.IsDetailsOpen);
        Assert.Equal(torrent, vm.SelectedTorrent);
        Assert.Equal(StoreNavSection.Downloads, vm.CurrentSection);

        vm.CloseDetailsCommand.Execute(null);
        Assert.False(vm.IsDetailsOpen);
        Assert.Null(vm.SelectedTorrent);
    }

    [Fact]
    public void LeftMenu_ContainsAddTorrentAndMagnetButtonsAfterHome()
    {
        var vm = new MainViewModel(new FakeTorrentEngineService(), new SettingsService(_tempDir), new ThemeService());

        Assert.Equal(StoreNavSection.Home, vm.TopNavItems[0].Section);
        Assert.Equal("Главная", vm.TopNavItems[0].Title);

        Assert.Equal(StoreNavSection.AddTorrent, vm.TopNavItems[1].Section);
        Assert.Equal("Добавить", vm.TopNavItems[1].Title);
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Add24, vm.TopNavItems[1].Icon);

        Assert.Equal(StoreNavSection.AddMagnet, vm.TopNavItems[2].Section);
        Assert.Equal("Magnet", vm.TopNavItems[2].Title);
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.Link24, vm.TopNavItems[2].Icon);

        vm.NavigateToCommand.Execute(StoreNavSection.AddMagnet);
        Assert.True(vm.IsAddMagnetModalOpen);
        Assert.Equal(StoreNavSection.Home, vm.CurrentSection);
    }

    [Fact]
    public void LeftMenu_ContainsCreateTorrentButtonAfterLibrary_AndOpensModal()
    {
        var settings = new SettingsService(_tempDir);
        settings.SaveSettings(new AppSettings
        {
            DefaultTrackers = ["udp://tracker.opentrackr.org:1337/announce"]
        });

        var vm = new MainViewModel(new FakeTorrentEngineService(), settings, new ThemeService());

        Assert.DoesNotContain(vm.TopNavItems, i => i.Section is StoreNavSection.Library or StoreNavSection.CreateTorrent);
        Assert.Equal(3, vm.BottomNavItems.Count);
        Assert.Equal(StoreNavSection.Library, vm.BottomNavItems[0].Section);
        Assert.Equal("Библиотека", vm.BottomNavItems[0].Title);
        Assert.Equal(StoreNavSection.CreateTorrent, vm.BottomNavItems[1].Section);
        Assert.Equal("Создать", vm.BottomNavItems[1].Title);
        Assert.Equal(Wpf.Ui.Controls.SymbolRegular.DocumentAdd24, vm.BottomNavItems[1].Icon);
        Assert.Equal(StoreNavSection.Settings, vm.BottomNavItems[2].Section);

        vm.NavigateToCommand.Execute(StoreNavSection.Library);
        Assert.True(vm.BottomNavItems[0].IsSelected);
        Assert.All(vm.TopNavItems, i => Assert.False(i.IsSelected));

        vm.NavigateToCommand.Execute(StoreNavSection.CreateTorrent);
        Assert.True(vm.IsCreateTorrentModalOpen);
        Assert.Equal(StoreNavSection.Library, vm.CurrentSection);
        Assert.Contains("tracker.opentrackr.org", vm.CreateTrackersText);
    }

    [Fact]
    public async Task BulkSelection_StartPauseAndRemoveSelectedTorrents_WorkAcrossMultipleItems()
    {
        var fakeEngine = new FakeTorrentEngineService();
        var t1 = new TorrentItemModel { InfoHash = "A", Name = "Alpha", StatusKind = TorrentStatusKind.Paused, Progress = 20 };
        var t2 = new TorrentItemModel { InfoHash = "B", Name = "Beta", StatusKind = TorrentStatusKind.Downloading, Progress = 50 };
        var t3 = new TorrentItemModel { InfoHash = "C", Name = "Gamma", StatusKind = TorrentStatusKind.Paused, Progress = 100 };
        fakeEngine.Torrents.Add(t1);
        fakeEngine.Torrents.Add(t2);
        fakeEngine.Torrents.Add(t3);

        var vm = new MainViewModel(fakeEngine, new SettingsService(_tempDir), new ThemeService());
        await vm.InitializeAsync();

        vm.UpdateSelectedTorrents([t1, t2]);
        Assert.Equal(2, vm.SelectedTorrentsCount);
        Assert.True(vm.HasMultipleSelectedTorrents);
        Assert.True(vm.CanStartSelectedTorrents);
        Assert.True(vm.CanPauseSelectedTorrents);
        Assert.Equal("Запустить (1)", vm.StartSelectedMenuHeader);
        Assert.Equal("Пауза (1)", vm.PauseSelectedMenuHeader);
        Assert.Equal("Удалить (2)", vm.RemoveSelectedMenuHeader);

        await vm.StartSelectedTorrentsCommand.ExecuteAsync(null);
        Assert.Equal(TorrentStatusKind.Downloading, t1.StatusKind);

        await vm.PauseSelectedTorrentsCommand.ExecuteAsync(null);
        Assert.Equal(TorrentStatusKind.Paused, t1.StatusKind);
        Assert.Equal(TorrentStatusKind.Paused, t2.StatusKind);

        vm.RequestRemoveSelectedTorrentsCommand.Execute(null);
        Assert.True(vm.IsRemoveModalOpen);
        Assert.Equal("Удалить выбранные торренты (2)?", vm.RemoveModalTitle);

        await vm.ConfirmRemoveTorrentCommand.ExecuteAsync(null);
        Assert.False(vm.IsRemoveModalOpen);
        Assert.Single(fakeEngine.Torrents);
        Assert.Equal("C", fakeEngine.Torrents[0].InfoHash);
        Assert.Empty(vm.SelectedTorrents);
    }

    [Fact]
    public async Task SeedingTorrent_ShowsStopNotPause_AndWhenStopped_BecomesCompletedAndVisibleOnlyInLibrary()
    {
        var fakeEngine = new FakeTorrentEngineService();
        var seeding = new TorrentItemModel
        {
            InfoHash = "SEED1",
            Name = "Ubuntu 26.04 ISO",
            StatusKind = TorrentStatusKind.Seeding,
            StatusText = "Раздача",
            Progress = 100
        };
        var downloading = new TorrentItemModel
        {
            InfoHash = "DL1",
            Name = "Fedora ISO",
            StatusKind = TorrentStatusKind.Downloading,
            StatusText = "Загрузка",
            Progress = 42
        };
        fakeEngine.Torrents.Add(seeding);
        fakeEngine.Torrents.Add(downloading);

        Assert.False(seeding.CanPause);
        Assert.True(seeding.CanStop);
        Assert.True(downloading.CanPause);
        Assert.False(downloading.CanStop);

        var vm = new MainViewModel(fakeEngine, new SettingsService(_tempDir), new ThemeService());
        await vm.InitializeAsync();

        vm.UpdateSelectedTorrents([seeding]);
        Assert.False(vm.CanPauseSelectedTorrents);
        Assert.True(vm.CanStopSelectedTorrents);
        Assert.Equal("Стоп", vm.StopSelectedMenuHeader);

        await vm.StopTorrentCommand.ExecuteAsync(seeding);

        Assert.Equal(TorrentStatusKind.Stopped, seeding.StatusKind);
        Assert.Equal("Завершен", seeding.StatusText);
        Assert.True(seeding.CanStart);
        Assert.False(seeding.CanStop);
        Assert.False(seeding.CanPause);

        Assert.DoesNotContain(seeding, vm.RecentTorrents);

        vm.NavigateToCommand.Execute(StoreNavSection.Downloads);
        Assert.DoesNotContain(seeding, vm.FilteredTorrents);

        vm.NavigateToCommand.Execute(StoreNavSection.Active);
        Assert.DoesNotContain(seeding, vm.FilteredTorrents);

        vm.NavigateToCommand.Execute(StoreNavSection.Seeding);
        Assert.DoesNotContain(seeding, vm.FilteredTorrents);

        vm.NavigateToCommand.Execute(StoreNavSection.Library);
        Assert.Contains(seeding, vm.FilteredTorrents);
    }

    [Fact]
    public void SortPeers_PlacesSeedersFirstSortedBySpeedThenPeersSortedBySpeed()
    {
        var peers = new[]
        {
            new TorrentPeerModel { Endpoint = "peer-slow", IsSeeder = false, DownloadSpeed = 0, UploadSpeed = 500 },
            new TorrentPeerModel { Endpoint = "seed-slow", IsSeeder = true, DownloadSpeed = 1_000, UploadSpeed = 0 },
            new TorrentPeerModel { Endpoint = "peer-fast", IsSeeder = false, DownloadSpeed = 50_000, UploadSpeed = 10_000 },
            new TorrentPeerModel { Endpoint = "seed-fast", IsSeeder = true, DownloadSpeed = 200_000, UploadSpeed = 0 }
        };

        var sorted = LibtorrentEngineService.SortPeers(peers).Select(p => p.Endpoint).ToArray();

        Assert.Equal(["seed-fast", "seed-slow", "peer-fast", "peer-slow"], sorted);
    }

    [Fact]
    public void TrafficStats_RecordAndBuildWindowAccumulateCorrectly()
    {
        var data = new TrafficStatsData();
        var today = new DateTime(2026, 11, 16, 13, 0, 0);

        LibtorrentEngineService.RecordTrafficDelta(data, 1_000_000, 250_000, today.AddDays(-2));
        LibtorrentEngineService.RecordTrafficDelta(data, 2_000_000, 500_000, today);
        LibtorrentEngineService.RecordTrafficDelta(data, 500_000, 100_000, today);

        Assert.Equal(3_500_000, data.TotalDownloadedBytes);
        Assert.Equal(850_000, data.TotalUploadedBytes);

        var window = LibtorrentEngineService.BuildRecentDaysWindow(data, today, 14);
        Assert.Equal(14, window.Count);
        Assert.Equal("2026-11-16", window[^1].DateKey);
        Assert.Equal(2_500_000, window[^1].DownloadedBytes);
        Assert.Equal(600_000, window[^1].UploadedBytes);
        Assert.Equal(1_000_000, window[^3].DownloadedBytes);
    }

    [Fact]
    public async Task EngineStatsUpdate_WhileDetailsOpenOnSeedingOrActive_KeepsDetailsOpen()
    {
        var fakeEngine = new FakeTorrentEngineService();
        var seeding = new TorrentItemModel
        {
            InfoHash = "SEED1",
            Name = "Linux ISO",
            StatusKind = TorrentStatusKind.Seeding,
            Progress = 100
        };
        fakeEngine.Torrents.Add(seeding);

        var vm = new MainViewModel(fakeEngine, new SettingsService(_tempDir), new ThemeService());
        await vm.InitializeAsync();

        vm.NavigateToCommand.Execute(StoreNavSection.Seeding);
        vm.SelectedTorrent = seeding;
        Assert.True(vm.IsDetailsOpen);

        // Simulate 4 periodic 1-second stats ticks
        for (int i = 0; i < 4; i++)
            fakeEngine.RaiseStatsUpdated();

        Assert.True(vm.IsDetailsOpen);
        Assert.Equal(seeding, vm.SelectedTorrent);

        vm.NavigateToCommand.Execute(StoreNavSection.Active);
        vm.SelectedTorrent = seeding;
        for (int i = 0; i < 4; i++)
            fakeEngine.RaiseStatsUpdated();

        Assert.True(vm.IsDetailsOpen);
        Assert.Equal(seeding, vm.SelectedTorrent);
    }

    [Fact]
    public async Task SubmitSearchOrMagnet_EmitsSystemNotification()
    {
        var fakeEngine = new FakeTorrentEngineService();
        var fakeNotifications = new FakeNotificationService();
        var vm = new MainViewModel(fakeEngine, new SettingsService(_tempDir), new ThemeService(), fakeNotifications);
        await vm.InitializeAsync();

        vm.SearchQuery = "0123456789ABCDEF0123456789ABCDEF01234567";
        await vm.SubmitSearchOrMagnetCommand.ExecuteAsync(null);

        Assert.Single(fakeNotifications.Messages);
        Assert.Equal(NotificationKind.Success, fakeNotifications.Messages[0].Kind);
    }

    [Fact]
    public async Task AddAndRemoveTrackerCommands_UpdateSelectedTorrentTrackers()
    {
        var fakeEngine = new FakeTorrentEngineService();
        var torrent = new TorrentItemModel { InfoHash = "T1", Name = "Ubuntu" };
        fakeEngine.Torrents.Add(torrent);

        var vm = new MainViewModel(fakeEngine, new SettingsService(_tempDir), new ThemeService());
        await vm.InitializeAsync();
        vm.OpenTorrentDetailsCommand.Execute(torrent);

        vm.NewTrackerInputText = "udp://tracker.opentrackr.org:1337/announce";
        await vm.AddTrackerToSelectedCommand.ExecuteAsync(null);

        Assert.Single(torrent.Trackers);
        Assert.Equal(string.Empty, vm.NewTrackerInputText);

        await vm.RemoveTrackerFromSelectedCommand.ExecuteAsync("udp://tracker.opentrackr.org:1337/announce");
        Assert.Empty(torrent.Trackers);
    }

    [Fact]
    public void PrimarySpeedDisplay_ShowsDownloadWhenDownloadingAndUploadWhenSeeding()
    {
        var item = new TorrentItemModel
        {
            StatusKind = TorrentStatusKind.Downloading,
            Progress = 25,
            DownloadSpeed = 2 * 1024 * 1024,
            UploadSpeed = 512 * 1024
        };

        Assert.StartsWith("↓ ", item.PrimarySpeedDisplay);
        Assert.Contains("2", item.PrimarySpeedDisplay);

        item.StatusKind = TorrentStatusKind.Seeding;
        item.Progress = 100;
        item.DownloadSpeed = 0;

        Assert.StartsWith("↑ ", item.PrimarySpeedDisplay);
        Assert.Contains("512", item.PrimarySpeedDisplay);
    }

    [Fact]
    public async Task SyncModel_WhenPaused_ZeroesFrozenSpeedsAndTotalEngineSpeeds()
    {
        string appData = Path.Combine(_tempDir, "appdata_pause");
        string dlDir = Path.Combine(_tempDir, "dl_pause");
        Directory.CreateDirectory(dlDir);

        var settingsService = new SettingsService(appData);
        settingsService.SaveSettings(new AppSettings
        {
            DefaultDownloadPath = dlDir,
            ListenPort = 0,
            EnableDht = false,
            EnableLocalPeerDiscovery = false,
            EnablePortForwarding = false
        });

        string torrentPath = Path.Combine(_tempDir, "pause_speed.torrent");
        File.WriteAllBytes(torrentPath, BencodeHelper.BuildMinimalTorrent("pause.bin", 262144, 262144L));

        await using var engine = new LibtorrentEngineService(settingsService);
        await engine.InitializeAsync();

        TorrentItemModel item = await engine.AddTorrentFileAsync(torrentPath, dlDir, startImmediately: true);
        item.DownloadSpeed = 12345;
        item.UploadSpeed = 6789;

        await engine.PauseAsync(item);

        Assert.Equal(TorrentStatusKind.Paused, item.StatusKind);
        Assert.Equal(0L, item.DownloadSpeed);
        Assert.Equal(0L, item.UploadSpeed);
        Assert.Equal(0L, engine.TotalDownloadSpeed);
        Assert.Equal(0L, engine.TotalUploadSpeed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private sealed class FakeTorrentEngineService : ITorrentEngineService
    {
        public ObservableCollection<TorrentItemModel> Torrents { get; } = new();
        public long TotalDownloadSpeed { get; set; }
        public long TotalUploadSpeed { get; set; }
        public long AllTimeDownloadedBytes { get; set; }
        public long AllTimeUploadedBytes { get; set; }
        public IReadOnlyList<DailyTrafficPoint> DailyTrafficHistory { get; set; } = [];
        public int DhtNodesCount { get; set; }
        public bool IsRunning => true;
        public event Action? StatsUpdated;

        public void RaiseStatsUpdated() => StatsUpdated?.Invoke();

        public Task InitializeAsync()
        {
            StatsUpdated?.Invoke();
            return Task.CompletedTask;
        }

        public Task<TorrentItemModel> AddTorrentFileAsync(string torrentFilePath, string? savePath = null, bool? startImmediately = null)
        {
            var item = new TorrentItemModel { InfoHash = Guid.NewGuid().ToString("N"), Name = Path.GetFileNameWithoutExtension(torrentFilePath) };
            Torrents.Add(item);
            return Task.FromResult(item);
        }

        public Task<TorrentItemModel> AddMagnetAsync(string magnetUri, string? savePath = null, bool? startImmediately = null)
        {
            var item = new TorrentItemModel
            {
                InfoHash = "0123456789ABCDEF0123456789ABCDEF01234567",
                Name = "Debian",
                MagnetUri = magnetUri,
                StatusKind = TorrentStatusKind.Metadata
            };
            Torrents.Add(item);
            return Task.FromResult(item);
        }

        public Task StartAsync(TorrentItemModel item)
        {
            item.StatusKind = item.IsCompleted ? TorrentStatusKind.Seeding : TorrentStatusKind.Downloading;
            return Task.CompletedTask;
        }

        public Task PauseAsync(TorrentItemModel item)
        {
            if (item.IsCompleted)
                return StopAsync(item);

            item.StatusKind = TorrentStatusKind.Paused;
            item.StatusText = "На паузе";
            return Task.CompletedTask;
        }
        public Task StopAsync(TorrentItemModel item)
        {
            item.StatusKind = TorrentStatusKind.Stopped;
            item.StatusText = item.IsCompleted ? "Завершен" : "Остановлен";
            return Task.CompletedTask;
        }
        public Task ForceRecheckAsync(TorrentItemModel item) => Task.CompletedTask;
        public Task ReannounceAsync(TorrentItemModel item) => Task.CompletedTask;
        public Task RemoveAsync(TorrentItemModel item, bool deleteFiles)
        {
            Torrents.Remove(item);
            return Task.CompletedTask;
        }
        public Task SetFilePriorityAsync(TorrentItemModel item, TorrentFileModel file, TorrentFilePriority priority) => Task.CompletedTask;
        public Task SetAllFilesPriorityAsync(TorrentItemModel item, TorrentFilePriority priority) => Task.CompletedTask;
        public Task AddTrackerAsync(TorrentItemModel item, string trackerUrl)
        {
            item.Trackers.Add(new TorrentTrackerModel { Uri = trackerUrl });
            return Task.CompletedTask;
        }
        public Task RemoveTrackerAsync(TorrentItemModel item, string trackerUri)
        {
            var match = item.Trackers.FirstOrDefault(t => t.Uri == trackerUri);
            if (match is not null) item.Trackers.Remove(match);
            return Task.CompletedTask;
        }
        public Task ApplySettingsAsync(AppSettings settings) => Task.CompletedTask;
        public Task RefreshDetailsAsync(TorrentItemModel item) => Task.CompletedTask;
        public Task ShutdownAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeNotificationService : INotificationService
    {
        public List<(string Title, string Message, NotificationKind Kind)> Messages { get; } = [];

        public void Show(string title, string message, NotificationKind kind = NotificationKind.Info)
            => Messages.Add((title, message, kind));

        public void Dispose() { }
    }
}
