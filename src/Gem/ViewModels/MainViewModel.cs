using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gem.Helpers;
using Gem.Models;
using Gem.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace Gem.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const int HistoryLength = 60;

    private readonly ITorrentEngineService _engine;
    private readonly SettingsService _settingsService;
    private readonly ThemeService _themeService;
    private readonly INotificationService? _notifications;
    private readonly IUpdateService? _updateService;
    private readonly IStartupService? _startupService;
    private readonly Queue<long> _downHistory = new();
    private readonly Queue<long> _upHistory = new();
    private readonly long[] _downHistoryBuffer = new long[HistoryLength];
    private readonly long[] _upHistoryBuffer = new long[HistoryLength];
    private IReadOnlyList<DailyTrafficPoint>? _lastTrafficHistory;
    private int _tickCounter;
    private bool _isLoadingSettings;
    private CancellationTokenSource? _createTorrentCts;

    public ObservableCollection<NavRailItem> TopNavItems { get; }

    public ObservableCollection<NavRailItem> BottomNavItems { get; }

    public NavRailItem SettingsNavItem { get; }

    public ObservableCollection<TorrentItemModel> FilteredTorrents { get; } = new();

    public ObservableCollection<TorrentItemModel> SelectedTorrents { get; } = new();

    public ObservableCollection<TorrentItemModel> RecentTorrents { get; } = new();

    public IReadOnlyList<TorrentFilePriority> FilePriorities { get; } =
    [
        TorrentFilePriority.High,
        TorrentFilePriority.Normal,
        TorrentFilePriority.Low,
        TorrentFilePriority.DoNotDownload
    ];

    public event Action<IReadOnlyList<long>, IReadOnlyList<long>>? SpeedHistoryUpdated;

    public event Action<IReadOnlyList<DailyTrafficPoint>>? TrafficHistoryUpdated;

    public Action<Action>? ThemeTransitionRunner { get; set; }

    [ObservableProperty]
    private StoreNavSection _currentSection = StoreNavSection.Home;

    [ObservableProperty]
    private string _currentSectionTitle = LocalizationService.Instance["NavHome"];

    [ObservableProperty]
    private TorrentFilterKind _selectedFilter = TorrentFilterKind.All;

    [ObservableProperty]
    private TorrentSortKind _selectedSort = TorrentSortKind.DateAddedDesc;

    public sealed partial class TorrentSortOption : ObservableObject
    {
        public TorrentSortKind Kind { get; }

        [ObservableProperty]
        private string _label;

        public TorrentSortOption(TorrentSortKind kind, string label)
        {
            Kind = kind;
            _label = label;
        }
    }

    public ObservableCollection<TorrentSortOption> SortOptions { get; } = new(BuildSortOptions());

    private static string GetSortOptionLabel(TorrentSortKind kind, LocalizationService loc) => kind switch
    {
        TorrentSortKind.DateAddedDesc => loc["SortDateAddedDesc"],
        TorrentSortKind.DateAddedAsc => loc["SortDateAddedAsc"],
        TorrentSortKind.NameAsc => loc["SortNameAsc"],
        TorrentSortKind.NameDesc => loc["SortNameDesc"],
        TorrentSortKind.SizeDesc => loc["SortSizeDesc"],
        TorrentSortKind.SizeAsc => loc["SortSizeAsc"],
        TorrentSortKind.ProgressDesc => loc["SortProgressDesc"],
        TorrentSortKind.DownloadSpeedDesc => loc["SortDownloadSpeedDesc"],
        TorrentSortKind.UploadSpeedDesc => loc["SortUploadSpeedDesc"],
        _ => loc["SortDateAddedDesc"]
    };

    private static TorrentSortOption[] BuildSortOptions()
    {
        var loc = LocalizationService.Instance;
        return
        [
            new(TorrentSortKind.DateAddedDesc, GetSortOptionLabel(TorrentSortKind.DateAddedDesc, loc)),
            new(TorrentSortKind.DateAddedAsc, GetSortOptionLabel(TorrentSortKind.DateAddedAsc, loc)),
            new(TorrentSortKind.NameAsc, GetSortOptionLabel(TorrentSortKind.NameAsc, loc)),
            new(TorrentSortKind.NameDesc, GetSortOptionLabel(TorrentSortKind.NameDesc, loc)),
            new(TorrentSortKind.SizeDesc, GetSortOptionLabel(TorrentSortKind.SizeDesc, loc)),
            new(TorrentSortKind.SizeAsc, GetSortOptionLabel(TorrentSortKind.SizeAsc, loc)),
            new(TorrentSortKind.ProgressDesc, GetSortOptionLabel(TorrentSortKind.ProgressDesc, loc)),
            new(TorrentSortKind.DownloadSpeedDesc, GetSortOptionLabel(TorrentSortKind.DownloadSpeedDesc, loc)),
            new(TorrentSortKind.UploadSpeedDesc, GetSortOptionLabel(TorrentSortKind.UploadSpeedDesc, loc))
        ];
    }

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private TorrentItemModel? _selectedTorrent;

    [ObservableProperty]
    private bool _isDetailsOpen;

    [ObservableProperty]
    private long _totalDownloadSpeed;

    [ObservableProperty]
    private long _totalUploadSpeed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllTimeRatio))]
    private long _allTimeDownloadedBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllTimeRatio))]
    private long _allTimeUploadedBytes;

    public double AllTimeRatio =>
        AllTimeDownloadedBytes > 0
            ? Math.Round((double)AllTimeUploadedBytes / AllTimeDownloadedBytes, 2)
            : 0;

    [ObservableProperty]
    private int _dhtNodesCount;

    [ObservableProperty]
    private int _totalTorrentsCount;

    [ObservableProperty]
    private int _downloadingCount;

    [ObservableProperty]
    private int _seedingCount;

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeToggleIcon))]
    [NotifyPropertyChangedFor(nameof(ThemeToggleTooltip))]
    private bool _isDarkTheme;

    public SymbolRegular ThemeToggleIcon =>
        IsDarkTheme ? SymbolRegular.WeatherSunny24 : SymbolRegular.WeatherMoon24;

    public string ThemeToggleTooltip =>
        IsDarkTheme ? LocalizationService.Instance["ThemeToggleToLight"] : LocalizationService.Instance["ThemeToggleToDark"];

    // Add Magnet Modal state
    [ObservableProperty]
    private bool _isAddMagnetModalOpen;

    [ObservableProperty]
    private string _magnetInputText = string.Empty;

    [ObservableProperty]
    private string _magnetSavePath = string.Empty;

    [ObservableProperty]
    private bool _magnetStartImmediately = true;

    // Create Torrent Modal state
    [ObservableProperty]
    private bool _isCreateTorrentModalOpen;

    [ObservableProperty]
    private string _createSourcePath = string.Empty;

    [ObservableProperty]
    private string _createSourceSummary = LocalizationService.Instance["CreateSourceDefault"];

    [ObservableProperty]
    private string _createTrackersText = string.Empty;

    [ObservableProperty]
    private string _createComment = string.Empty;

    [ObservableProperty]
    private bool _createIsPrivate;

    [ObservableProperty]
    private bool _createStartSeeding = true;

    [ObservableProperty]
    private int _createSelectedPieceSizeIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditCreateTorrentFields))]
    private bool _isCreatingTorrent;

    [ObservableProperty]
    private double _createTorrentProgress;

    public bool CanEditCreateTorrentFields => !IsCreatingTorrent;

    public ObservableCollection<TorrentPieceSizeOption> CreatePieceSizeOptions { get; } = new(BuildCreatePieceSizeOptions());

    public sealed record TorrentPieceSizeOption(string Label, int Bytes);

    private static TorrentPieceSizeOption[] BuildCreatePieceSizeOptions() =>
    [
        new(LocalizationService.Instance["CreatePieceAuto"], 0),
        new(FormatHelper.FormatBytes(64 * 1024), 64 * 1024),
        new(FormatHelper.FormatBytes(256 * 1024), 256 * 1024),
        new(FormatHelper.FormatBytes(512 * 1024), 512 * 1024),
        new(FormatHelper.FormatBytes(1024 * 1024), 1024 * 1024),
        new(FormatHelper.FormatBytes(2L * 1024 * 1024), 2 * 1024 * 1024),
        new(FormatHelper.FormatBytes(4L * 1024 * 1024), 4 * 1024 * 1024),
        new(FormatHelper.FormatBytes(8L * 1024 * 1024), 8 * 1024 * 1024),
        new(FormatHelper.FormatBytes(16L * 1024 * 1024), 16 * 1024 * 1024)
    ];

    // Remove Confirmation Modal state
    private List<TorrentItemModel> _torrentsPendingRemoval = [];

    [ObservableProperty]
    private bool _isRemoveModalOpen;

    [ObservableProperty]
    private TorrentItemModel? _torrentPendingRemoval;

    [ObservableProperty]
    private string _removeModalTitle = LocalizationService.Instance["RemoveModalTitleSingle"];

    [ObservableProperty]
    private string _removeModalSummary = string.Empty;

    [ObservableProperty]
    private bool _deleteFilesOnRemove;

    public int SelectedTorrentsCount => SelectedTorrents.Count;

    public bool HasSelectedTorrents => SelectedTorrents.Count > 0;

    public bool HasMultipleSelectedTorrents => SelectedTorrents.Count > 1;

    public bool IsSingleTorrentSelected => SelectedTorrents.Count == 1;

    public bool CanStartSelectedTorrents => SelectedTorrents.Any(t => t.CanStart);

    public bool CanPauseSelectedTorrents => SelectedTorrents.Any(t => t.CanPause);

    public bool CanStopSelectedTorrents => SelectedTorrents.Any(t => t.CanStop);

    public bool CanStopOrPauseSelectedTorrents => SelectedTorrents.Any(t => t.CanPause || t.CanStop);

    public bool HasSelectedIncompleteTorrents => SelectedTorrents.Any(t => !t.IsCompleted);

    public bool AreSelectedDownloadsSeedEnabled
    {
        get
        {
            var downloads = SelectedTorrents.Where(t => !t.IsCompleted).ToList();
            bool globalDefault = _settingsService.Current.SeedAfterDownload;
            return downloads.Count == 0
                ? globalDefault
                : downloads.All(t => t.GetEffectiveSeedAfterDownload(globalDefault));
        }
        set => SetSelectedDownloadsSeedAfterDownload(value);
    }

    public bool IsSelectedDownloadsSleepChecked
    {
        get => AreAllSelectedDownloadsUsingPostAction(PostCompletionAction.Sleep);
        set => SetSelectedDownloadsPostAction(value ? PostCompletionAction.Sleep : PostCompletionAction.None);
    }

    public bool IsSelectedDownloadsHibernateChecked
    {
        get => AreAllSelectedDownloadsUsingPostAction(PostCompletionAction.Hibernate);
        set => SetSelectedDownloadsPostAction(value ? PostCompletionAction.Hibernate : PostCompletionAction.None);
    }

    public bool IsSelectedDownloadsShutdownChecked
    {
        get => AreAllSelectedDownloadsUsingPostAction(PostCompletionAction.Shutdown);
        set => SetSelectedDownloadsPostAction(value ? PostCompletionAction.Shutdown : PostCompletionAction.None);
    }

    public Action<string>? ClipboardTextWriter { get; set; }

    public string StartSelectedMenuHeader =>
        SelectedTorrents.Count > 1
            ? LocalizationService.Instance.Format("TorrentsActionStartCount", SelectedTorrents.Count(t => t.CanStart))
            : LocalizationService.Instance["TorrentsActionStart"];

    public string PauseSelectedMenuHeader =>
        SelectedTorrents.Count > 1
            ? LocalizationService.Instance.Format("TorrentsActionPauseCount", SelectedTorrents.Count(t => t.CanPause))
            : LocalizationService.Instance["TorrentsActionPause"];

    public string StopSelectedMenuHeader =>
        SelectedTorrents.Count > 1
            ? LocalizationService.Instance.Format("TorrentsActionStopCount", SelectedTorrents.Count(t => t.CanStop))
            : LocalizationService.Instance["TorrentsActionStop"];

    public string StopOrPauseSelectedMenuHeader =>
        SelectedTorrents.Count > 1
            ? LocalizationService.Instance.Format("TorrentsActionStopCount", SelectedTorrents.Count(t => t.CanPause || t.CanStop))
            : LocalizationService.Instance["TorrentsActionStop"];

    public string RemoveSelectedMenuHeader =>
        SelectedTorrents.Count > 1
            ? LocalizationService.Instance.Format("TorrentsActionDeleteCount", SelectedTorrents.Count)
            : LocalizationService.Instance["TorrentsActionDelete"];

    // Settings editor fields
    public IReadOnlyList<LanguageOption> AvailableLanguages => LocalizationService.Instance.SupportedLanguages;

    [ObservableProperty]
    private string _settingsLanguage = LocalizationService.Instance.CurrentLanguageCode;

    [ObservableProperty]
    private string _settingsDownloadPath = string.Empty;

    [ObservableProperty]
    private AppThemeMode _settingsThemeMode = AppThemeMode.System;

    [ObservableProperty]
    private AppColorTheme _settingsColorTheme = AppColorTheme.Mint;

    public IReadOnlyList<ColorThemeSwatchOption> ColorThemeOptions { get; } = BuildColorThemeOptions();

    [ObservableProperty]
    private int _settingsListenPort = 55123;

    [ObservableProperty]
    private bool _settingsEnableDht = true;

    [ObservableProperty]
    private bool _settingsEnableLpd = true;

    [ObservableProperty]
    private bool _settingsEnablePortForwarding = true;

    [ObservableProperty]
    private int _settingsMaxDownloadKBps;

    [ObservableProperty]
    private int _settingsMaxUploadKBps;

    [ObservableProperty]
    private int _settingsMaxConnections = 150;

    [ObservableProperty]
    private int _settingsReannounceIntervalSeconds;

    [ObservableProperty]
    private string _settingsDefaultTrackersText = string.Empty;

    [ObservableProperty]
    private string _newTrackerInputText = string.Empty;

    [ObservableProperty]
    private bool _settingsAutoStart = true;

    [ObservableProperty]
    private bool _settingsSeedAfterDownload = true;

    [ObservableProperty]
    private bool _settingsMinimizeToTrayOnClose = true;

    [ObservableProperty]
    private bool _settingsStartMinimized;

    [ObservableProperty]
    private bool _settingsLaunchAtStartup;

    public bool ShouldMinimizeToTrayOnClose => SettingsMinimizeToTrayOnClose;

    [ObservableProperty]
    private string _settingsUpdateRepository = string.Empty;

    [ObservableProperty]
    private bool _settingsAutoCheckUpdates = true;

    [ObservableProperty]
    private bool _isCheckingForUpdates;

    [ObservableProperty]
    private bool _isDownloadingUpdate;

    [ObservableProperty]
    private double _updateDownloadProgress;

    [ObservableProperty]
    private string _updateStatusText = LocalizationService.Instance["UpdateStatusNotChecked"];

    [ObservableProperty]
    private UpdateReleaseInfo? _availableUpdate;

    [ObservableProperty]
    private bool _isUpdateBannerVisible;

    public bool HasAvailableUpdate => AvailableUpdate is not null;

    public string CurrentVersionText =>
        $"v{(_updateService?.CurrentVersion ?? typeof(MainViewModel).Assembly.GetName().Version ?? new Version(1, 0, 2)).ToString(3)}";

    public string AppWindowTitle => $"Gem {CurrentVersionText}";

    partial void OnAvailableUpdateChanged(UpdateReleaseInfo? value)
    {
        OnPropertyChanged(nameof(HasAvailableUpdate));
    }

    public bool IsHomeView => CurrentSection == StoreNavSection.Home;

    public bool IsTorrentsView => CurrentSection is StoreNavSection.Downloads or StoreNavSection.Active or StoreNavSection.Seeding or StoreNavSection.Library;

    public bool IsSettingsView => CurrentSection == StoreNavSection.Settings;

    public bool IsLibraryView => CurrentSection == StoreNavSection.Library;

    public bool IsDownloadsView => CurrentSection == StoreNavSection.Downloads;

    private readonly TaskCompletionSource _initializedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitForInitializedAsync() => _initializedTcs.Task;

    public MainViewModel(
        ITorrentEngineService engine,
        SettingsService settingsService,
        ThemeService themeService,
        INotificationService? notifications = null,
        IUpdateService? updateService = null,
        IStartupService? startupService = null)
    {
        _engine = engine;
        _settingsService = settingsService;
        _themeService = themeService;
        _notifications = notifications;
        _updateService = updateService;
        _startupService = startupService;

        var loc = LocalizationService.Instance;
        TopNavItems =
        [
            new NavRailItem { Section = StoreNavSection.Home, Title = loc["NavHome"], Icon = SymbolRegular.Home24, IsSelected = true },
            new NavRailItem { Section = StoreNavSection.AddTorrent, Title = loc["NavAddTorrent"], Icon = SymbolRegular.Add24 },
            new NavRailItem { Section = StoreNavSection.AddMagnet, Title = "Magnet", Icon = SymbolRegular.Link24 },
            new NavRailItem { Section = StoreNavSection.Downloads, Title = loc["NavDownloads"], Icon = SymbolRegular.ArrowDownload24 },
            new NavRailItem { Section = StoreNavSection.Active, Title = loc["NavActive"], Icon = SymbolRegular.Pulse24 },
            new NavRailItem { Section = StoreNavSection.Seeding, Title = loc["NavSeeding"], Icon = SymbolRegular.ArrowUpload24 }
        ];

        SettingsNavItem = new NavRailItem
        {
            Section = StoreNavSection.Settings,
            Title = loc["NavSettings"],
            Icon = SymbolRegular.Settings24,
            IsBottomPinned = true
        };

        BottomNavItems =
        [
            new NavRailItem { Section = StoreNavSection.Library, Title = loc["NavLibrary"], Icon = SymbolRegular.Library24, IsBottomPinned = true },
            new NavRailItem { Section = StoreNavSection.CreateTorrent, Title = loc["NavCreateTorrent"], Icon = SymbolRegular.DocumentAdd24, IsBottomPinned = true },
            SettingsNavItem
        ];

        for (int i = 0; i < HistoryLength; i++)
        {
            _downHistory.Enqueue(0);
            _upHistory.Enqueue(0);
        }

        _engine.Torrents.CollectionChanged += OnTorrentsCollectionChanged;
        _engine.StatsUpdated += OnEngineStatsUpdated;
        _engine.ListenPortChanged += OnEngineListenPortChanged;
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
        UpdateColorThemeSwatchSelection(SettingsColorTheme);
    }

    public async Task InitializeAsync(Window? mainWindow = null)
    {
        try
        {
            var settings = _settingsService.LoadSettings();
            LocalizationService.Instance.SetLanguage(settings.Language);
            LoadSettingsIntoEditor(settings);
            _themeService.ApplyTheme(settings.ThemeMode, settings.ColorTheme, mainWindow);
            IsDarkTheme = _themeService.IsDarkActive;

            await _engine.InitializeAsync();
            RebuildFilteredViews();
            UpdateCounts();

            if (_updateService is not null &&
                settings.AutoCheckUpdatesOnStartup &&
                GitHubUpdateService.TryNormalizeRepository(settings.UpdateRepository) is not null)
            {
                _ = CheckForUpdatesInternalAsync(settings.UpdateRepository, notifyIfUpToDateOrError: false);
            }
        }
        finally
        {
            _initializedTcs.TrySetResult();
        }
    }

    public async Task ShutdownAsync()
    {
        _engine.ListenPortChanged -= OnEngineListenPortChanged;
        LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
        await _engine.ShutdownAsync();
    }

    private void LoadSettingsIntoEditor(AppSettings s)
    {
        _isLoadingSettings = true;
        try
        {
            SettingsDownloadPath = s.DefaultDownloadPath;
            SettingsThemeMode = s.ThemeMode;
            SettingsColorTheme = s.ColorTheme;
            UpdateColorThemeSwatchSelection(s.ColorTheme);
            SettingsLanguage = LocalizationService.ResolveSupportedLanguageCode(s.Language);
            SettingsListenPort = s.ListenPort;
            SettingsEnableDht = s.EnableDht;
            SettingsEnableLpd = s.EnableLocalPeerDiscovery;
            SettingsEnablePortForwarding = s.EnablePortForwarding;
            SettingsMaxDownloadKBps = s.MaxDownloadSpeedKBps;
            SettingsMaxUploadKBps = s.MaxUploadSpeedKBps;
            SettingsMaxConnections = s.MaxConnections;
            SettingsReannounceIntervalSeconds = s.ReannounceIntervalSeconds;
            SettingsDefaultTrackersText = string.Join(Environment.NewLine, s.DefaultTrackers);
            SettingsAutoStart = s.AutoStartOnAdd;
            SettingsSeedAfterDownload = s.SeedAfterDownload;
            SettingsMinimizeToTrayOnClose = s.MinimizeToTrayOnClose;
            SettingsStartMinimized = s.StartMinimized;
            SelectedSort = s.SortKind;
            if (_startupService is not null)
            {
                bool regEnabled = _startupService.IsStartupEnabled();
                if (regEnabled)
                {
                    _startupService.SetStartupEnabled(true);
                }

                SettingsLaunchAtStartup = regEnabled;
                if (s.LaunchAtStartup != regEnabled)
                {
                    s.LaunchAtStartup = regEnabled;
                    _settingsService.SaveSettings(s);
                }
            }
            else
            {
                SettingsLaunchAtStartup = s.LaunchAtStartup;
            }
            SettingsUpdateRepository = s.UpdateRepository;
            SettingsAutoCheckUpdates = s.AutoCheckUpdatesOnStartup;
            MagnetSavePath = s.DefaultDownloadPath;
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    partial void OnSettingsLanguageChanged(string value)
    {
        if (_isLoadingSettings || string.IsNullOrWhiteSpace(value))
            return;

        string resolved = LocalizationService.Instance.SetLanguage(value);
        if (!string.Equals(_settingsService.Current.Language, resolved, StringComparison.OrdinalIgnoreCase))
        {
            _settingsService.Current.Language = resolved;
            _settingsService.SaveSettings(_settingsService.Current);
        }
    }

    partial void OnSettingsColorThemeChanged(AppColorTheme value)
    {
        UpdateColorThemeSwatchSelection(value);

        if (_isLoadingSettings)
            return;

        if (_settingsService.Current.ColorTheme != value)
        {
            _settingsService.Current.ColorTheme = value;
            _settingsService.SaveSettings(_settingsService.Current);
        }

        _themeService.ApplyTheme(SettingsThemeMode, value, Application.Current?.MainWindow);
        IsDarkTheme = _themeService.IsDarkActive;
        NotifyThemeBrushesRefreshed();
    }

    private void UpdateColorThemeSwatchSelection(AppColorTheme activeTheme)
    {
        foreach (var opt in ColorThemeOptions)
            opt.IsSelected = opt.Theme == activeTheme;
    }

    private static IReadOnlyList<ColorThemeSwatchOption> BuildColorThemeOptions()
    {
        var list = new List<ColorThemeSwatchOption>();
        foreach (var theme in Enum.GetValues<AppColorTheme>())
        {
            (Color primary, Color secondary) = ThemeService.GetPalette(theme);
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            brush.GradientStops.Add(new GradientStop(primary, 0.0));
            brush.GradientStops.Add(new GradientStop(primary, 0.495));
            brush.GradientStops.Add(new GradientStop(secondary, 0.505));
            brush.GradientStops.Add(new GradientStop(secondary, 1.0));
            brush.Freeze();

            list.Add(new ColorThemeSwatchOption
            {
                Theme = theme,
                SwatchBrush = brush
            });
        }
        return list;
    }

    partial void OnSettingsStartMinimizedChanged(bool value)
    {
        if (_isLoadingSettings)
            return;

        if (_settingsService.Current.StartMinimized != value)
        {
            _settingsService.Current.StartMinimized = value;
            _settingsService.SaveSettings(_settingsService.Current);
        }
    }

    private void OnEngineListenPortChanged(int newPort)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ApplyUpdatedListenPort(newPort));
            return;
        }

        ApplyUpdatedListenPort(newPort);
    }

    private void ApplyUpdatedListenPort(int newPort)
    {
        if (newPort is >= 1024 and <= 65535 && SettingsListenPort != newPort)
            SettingsListenPort = newPort;
    }

    [RelayCommand]
    private void SelectColorTheme(AppColorTheme colorTheme)
    {
        SettingsColorTheme = colorTheme;
    }

    private void NotifyThemeBrushesRefreshed()
    {
        foreach (var torrent in _engine.Torrents)
            torrent.NotifyThemeColorsChanged();

        SpeedHistoryUpdated?.Invoke(_downHistory.ToArray(), _upHistory.ToArray());
        TrafficHistoryUpdated?.Invoke(_engine.DailyTrafficHistory);
    }

    partial void OnSettingsLaunchAtStartupChanged(bool value)
    {
        if (_isLoadingSettings)
            return;

        if (_startupService is not null && !_startupService.SetStartupEnabled(value))
        {
            _isLoadingSettings = true;
            try
            {
                SettingsLaunchAtStartup = _startupService.IsStartupEnabled();
            }
            finally
            {
                _isLoadingSettings = false;
            }
            return;
        }

        if (_settingsService.Current.LaunchAtStartup != value)
        {
            _settingsService.Current.LaunchAtStartup = value;
            _settingsService.SaveSettings(_settingsService.Current);
        }
    }

    private void OnLanguageChanged()
    {
        var loc = LocalizationService.Instance;
        foreach (var item in TopNavItems)
            item.Title = GetNavTitle(item.Section, loc);
        foreach (var item in BottomNavItems)
            item.Title = GetNavTitle(item.Section, loc);

        CurrentSectionTitle = GetSectionTitle(CurrentSection, loc);
        OnPropertyChanged(nameof(ThemeToggleTooltip));

        int prevPieceIdx = CreateSelectedPieceSizeIndex;
        var updatedOptions = BuildCreatePieceSizeOptions();
        CreatePieceSizeOptions.Clear();
        foreach (var opt in updatedOptions)
            CreatePieceSizeOptions.Add(opt);
        CreateSelectedPieceSizeIndex = Math.Clamp(prevPieceIdx, 0, Math.Max(0, CreatePieceSizeOptions.Count - 1));
        CreateSourceSummary = BuildCreateSourceSummary(CreateSourcePath);

        foreach (var opt in SortOptions)
            opt.Label = GetSortOptionLabel(opt.Kind, loc);

        if (!IsCheckingForUpdates && !IsDownloadingUpdate && AvailableUpdate is null)
            UpdateStatusText = loc["UpdateStatusNotChecked"];

        RefreshSelectionProperties();
        foreach (var torrent in _engine.Torrents)
            torrent.NotifyLocalizedTextsChanged();

        SpeedHistoryUpdated?.Invoke(_downHistory.ToArray(), _upHistory.ToArray());
        TrafficHistoryUpdated?.Invoke(_engine.DailyTrafficHistory);

        if (SelectedTorrent is not null)
            _ = _engine.RefreshDetailsAsync(SelectedTorrent);
    }

    private static string GetNavTitle(StoreNavSection section, LocalizationService loc) => section switch
    {
        StoreNavSection.Home => loc["NavHome"],
        StoreNavSection.AddTorrent => loc["NavAddTorrent"],
        StoreNavSection.AddMagnet => "Magnet",
        StoreNavSection.Downloads => loc["NavDownloads"],
        StoreNavSection.Active => loc["NavActive"],
        StoreNavSection.Seeding => loc["NavSeeding"],
        StoreNavSection.Library => loc["NavLibrary"],
        StoreNavSection.CreateTorrent => loc["NavCreateTorrent"],
        StoreNavSection.Settings => loc["NavSettings"],
        _ => loc["NavHome"]
    };

    private static string GetSectionTitle(StoreNavSection section, LocalizationService loc) => section switch
    {
        StoreNavSection.Home => loc["NavHome"],
        StoreNavSection.Downloads => loc["NavDownloads"],
        StoreNavSection.Active => loc["NavActive"],
        StoreNavSection.Seeding => loc["NavSeeding"],
        StoreNavSection.Library => loc["NavLibrary"],
        StoreNavSection.Settings => loc["NavSettings"],
        _ => loc["NavHome"]
    };

    partial void OnSettingsMinimizeToTrayOnCloseChanged(bool value)
    {
        if (_isLoadingSettings)
            return;

        _settingsService.Current.MinimizeToTrayOnClose = value;
        _settingsService.SaveSettings(_settingsService.Current);
    }

    private void OnTorrentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildFilteredViews();
        UpdateCounts();
    }

    private void OnEngineStatsUpdated()
    {
        TotalDownloadSpeed = _engine.TotalDownloadSpeed;
        TotalUploadSpeed = _engine.TotalUploadSpeed;
        AllTimeDownloadedBytes = _engine.AllTimeDownloadedBytes;
        AllTimeUploadedBytes = _engine.AllTimeUploadedBytes;
        DhtNodesCount = _engine.DhtNodesCount;

        if (_downHistory.Count >= HistoryLength)
            _downHistory.Dequeue();
        if (_upHistory.Count >= HistoryLength)
            _upHistory.Dequeue();

        _downHistory.Enqueue(TotalDownloadSpeed);
        _upHistory.Enqueue(TotalUploadSpeed);

        IReadOnlyList<long> downSeries;
        IReadOnlyList<long> upSeries;
        if (_downHistory.Count == HistoryLength && _upHistory.Count == HistoryLength)
        {
            _downHistory.CopyTo(_downHistoryBuffer, 0);
            _upHistory.CopyTo(_upHistoryBuffer, 0);
            downSeries = _downHistoryBuffer;
            upSeries = _upHistoryBuffer;
        }
        else
        {
            downSeries = _downHistory.ToArray();
            upSeries = _upHistory.ToArray();
        }

        SpeedHistoryUpdated?.Invoke(downSeries, upSeries);

        var currentTraffic = _engine.DailyTrafficHistory;
        if (!ReferenceEquals(_lastTrafficHistory, currentTraffic))
        {
            _lastTrafficHistory = currentTraffic;
            TrafficHistoryUpdated?.Invoke(currentTraffic);
        }

        UpdateCounts();

        if (SelectedTorrent is not null && IsDetailsOpen)
        {
            _ = _engine.RefreshDetailsAsync(SelectedTorrent);
        }

        _tickCounter++;
        if (_tickCounter % 2 == 0 && !IsDetailsOpen)
        {
            if (CurrentSection is StoreNavSection.Downloads or StoreNavSection.Active or StoreNavSection.Seeding or StoreNavSection.Library)
            {
                RebuildFilteredViews();
            }
        }
    }

    private void UpdateCounts()
    {
        var all = _engine.Torrents;
        TotalTorrentsCount = all.Count;
        DownloadingCount = all.Count(t => t.StatusKind is TorrentStatusKind.Downloading or TorrentStatusKind.Metadata or TorrentStatusKind.Checking);
        SeedingCount = all.Count(t => t.StatusKind == TorrentStatusKind.Seeding);
        CompletedCount = all.Count(t => t.IsCompleted);

        int activeCount = all.Count(t => t.IsActiveTransfer);

        foreach (var item in TopNavItems)
        {
            item.BadgeCount = item.Section switch
            {
                StoreNavSection.Downloads => DownloadingCount,
                StoreNavSection.Active => activeCount,
                StoreNavSection.Seeding => SeedingCount,
                _ => 0
            };
        }
    }

    partial void OnCurrentSectionChanged(StoreNavSection value)
    {
        foreach (var item in TopNavItems)
            item.IsSelected = item.Section == value;

        foreach (var item in BottomNavItems)
            item.IsSelected = item.Section == value;

        CurrentSectionTitle = GetSectionTitle(value, LocalizationService.Instance);

        OnPropertyChanged(nameof(IsHomeView));
        OnPropertyChanged(nameof(IsTorrentsView));
        OnPropertyChanged(nameof(IsSettingsView));
        OnPropertyChanged(nameof(IsLibraryView));
        OnPropertyChanged(nameof(IsDownloadsView));

        SelectedTorrent = null;
        IsDetailsOpen = false;
        ClearSelectedTorrents();

        RebuildFilteredViews();
    }

    partial void OnSelectedFilterChanged(TorrentFilterKind value) => RebuildFilteredViews();

    partial void OnSelectedSortChanged(TorrentSortKind value)
    {
        if (!_isLoadingSettings && _settingsService.Current.SortKind != value)
        {
            _settingsService.Current.SortKind = value;
            _settingsService.SaveSettings(_settingsService.Current);
        }

        RebuildFilteredViews();
    }

    partial void OnSearchQueryChanged(string value)
    {
        if (FormatHelper.NormalizeToMagnetUri(value) is null)
            RebuildFilteredViews();
    }

    partial void OnSelectedTorrentChanged(TorrentItemModel? value)
    {
        IsDetailsOpen = value is not null;
        if (value is not null)
        {
            _ = _engine.RefreshDetailsAsync(value);
        }
    }

    private void RebuildFilteredViews()
    {
        var desiredRecent = _engine.Torrents
            .Where(t => !(t.IsCompleted && t.StatusKind == TorrentStatusKind.Stopped))
            .OrderByDescending(t => t.AddedAt)
            .Take(10)
            .ToList();
        SyncCollectionInPlace(RecentTorrents, desiredRecent);

        IEnumerable<TorrentItemModel> query = _engine.Torrents;

        query = CurrentSection switch
        {
            StoreNavSection.Active => query.Where(t => t.IsActiveTransfer),
            StoreNavSection.Seeding => query.Where(t => t.StatusKind == TorrentStatusKind.Seeding),
            StoreNavSection.Library => query.Where(t => t.IsCompleted),
            StoreNavSection.Downloads => SelectedFilter switch
            {
                TorrentFilterKind.Downloading => query.Where(t => !t.IsCompleted && t.StatusKind is TorrentStatusKind.Downloading or TorrentStatusKind.Metadata or TorrentStatusKind.Checking),
                TorrentFilterKind.Paused => query.Where(t => !t.IsCompleted && t.StatusKind is TorrentStatusKind.Paused or TorrentStatusKind.Stopped),
                TorrentFilterKind.Errors => query.Where(t => !t.IsCompleted && t.StatusKind == TorrentStatusKind.Error),
                _ => query.Where(t => !t.IsCompleted && t.StatusKind != TorrentStatusKind.Seeding)
            },
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(SearchQuery) && FormatHelper.NormalizeToMagnetUri(SearchQuery) is null)
        {
            string term = SearchQuery.Trim();
            query = query.Where(t => t.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                     t.InfoHash.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        query = ApplySort(query, SelectedSort);

        var result = query.ToList();
        SyncCollectionInPlace(FilteredTorrents, result);
        RefreshSelectionProperties();
    }

    internal static IEnumerable<TorrentItemModel> ApplySort(IEnumerable<TorrentItemModel> query, TorrentSortKind sortKind) => sortKind switch
    {
        TorrentSortKind.DateAddedAsc => query.OrderBy(t => t.AddedAt).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase),
        TorrentSortKind.NameAsc => query.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(t => t.AddedAt),
        TorrentSortKind.NameDesc => query.OrderByDescending(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(t => t.AddedAt),
        TorrentSortKind.SizeDesc => query.OrderByDescending(t => t.TotalSize).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase),
        TorrentSortKind.SizeAsc => query.OrderBy(t => t.TotalSize).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase),
        TorrentSortKind.ProgressDesc => query.OrderByDescending(t => t.Progress).ThenByDescending(t => t.AddedAt),
        TorrentSortKind.DownloadSpeedDesc => query.OrderByDescending(t => t.DownloadSpeed).ThenByDescending(t => t.AddedAt),
        TorrentSortKind.UploadSpeedDesc => query.OrderByDescending(t => t.UploadSpeed).ThenByDescending(t => t.AddedAt),
        _ => query.OrderByDescending(t => t.AddedAt).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
    };

    public void UpdateSelectedTorrents(IEnumerable<TorrentItemModel> selectedItems)
    {
        var next = selectedItems.Distinct().ToList();
        SelectedTorrents.Clear();
        foreach (var item in next)
            SelectedTorrents.Add(item);

        RefreshSelectionProperties();
    }

    [RelayCommand]
    private void ClearSelectedTorrents()
    {
        if (SelectedTorrents.Count == 0)
            return;

        SelectedTorrents.Clear();
        RefreshSelectionProperties();
    }

    private void RefreshSelectionProperties()
    {
        OnPropertyChanged(nameof(SelectedTorrentsCount));
        OnPropertyChanged(nameof(HasSelectedTorrents));
        OnPropertyChanged(nameof(HasMultipleSelectedTorrents));
        OnPropertyChanged(nameof(IsSingleTorrentSelected));
        OnPropertyChanged(nameof(CanStartSelectedTorrents));
        OnPropertyChanged(nameof(CanPauseSelectedTorrents));
        OnPropertyChanged(nameof(CanStopSelectedTorrents));
        OnPropertyChanged(nameof(CanStopOrPauseSelectedTorrents));
        OnPropertyChanged(nameof(HasSelectedIncompleteTorrents));
        OnPropertyChanged(nameof(AreSelectedDownloadsSeedEnabled));
        OnPropertyChanged(nameof(IsSelectedDownloadsSleepChecked));
        OnPropertyChanged(nameof(IsSelectedDownloadsHibernateChecked));
        OnPropertyChanged(nameof(IsSelectedDownloadsShutdownChecked));
        OnPropertyChanged(nameof(StartSelectedMenuHeader));
        OnPropertyChanged(nameof(PauseSelectedMenuHeader));
        OnPropertyChanged(nameof(StopSelectedMenuHeader));
        OnPropertyChanged(nameof(StopOrPauseSelectedMenuHeader));
        OnPropertyChanged(nameof(RemoveSelectedMenuHeader));
    }

    private static void SyncCollectionInPlace(ObservableCollection<TorrentItemModel> target, IReadOnlyList<TorrentItemModel> desired)
    {
        var desiredSet = new HashSet<TorrentItemModel>(desired);
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(target[i]))
                target.RemoveAt(i);
        }

        for (int i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (i < target.Count && ReferenceEquals(target[i], item))
                continue;

            int existingIdx = target.IndexOf(item);
            if (existingIdx >= 0)
                target.Move(existingIdx, i);
            else
                target.Insert(i, item);
        }
    }

    [RelayCommand]
    private async Task NavigateToAsync(StoreNavSection section)
    {
        if (section == StoreNavSection.AddTorrent)
        {
            await AddTorrentFileAsync();
            return;
        }

        if (section == StoreNavSection.AddMagnet)
        {
            OpenAddMagnetModal();
            return;
        }

        if (section == StoreNavSection.CreateTorrent)
        {
            OpenCreateTorrentModal();
            return;
        }

        if (CurrentSection == section)
        {
            if (IsDetailsOpen)
                CloseDetails();
            return;
        }

        CurrentSection = section;
    }

    [RelayCommand]
    private void SetFilter(TorrentFilterKind filter)
    {
        SelectedFilter = filter;
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        var window = Application.Current?.MainWindow;
        void Apply()
        {
            var next = _themeService.ToggleLightDark(window);
            SettingsThemeMode = next;
            IsDarkTheme = _themeService.IsDarkActive;

            var s = _settingsService.Current;
            s.ThemeMode = next;
            _settingsService.SaveSettings(s);
        }

        if (ThemeTransitionRunner is not null)
            ThemeTransitionRunner(Apply);
        else
            Apply();
    }

    [RelayCommand]
    private async Task SubmitSearchOrMagnetAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
            return;

        if (FormatHelper.NormalizeToMagnetUri(SearchQuery) is { } magnet)
        {
            SearchQuery = string.Empty;
            await AddMagnetInternalAsync(magnet, _settingsService.Current.DefaultDownloadPath, _settingsService.Current.AutoStartOnAdd);
            CurrentSection = StoreNavSection.Downloads;
        }
        else if (CurrentSection == StoreNavSection.Home || CurrentSection == StoreNavSection.Settings)
        {
            CurrentSection = StoreNavSection.Downloads;
        }
    }

    [RelayCommand]
    private async Task AddTorrentFileAsync()
    {
        var loc = LocalizationService.Instance;
        var dialog = new OpenFileDialog
        {
            Title = loc["DialogSelectTorrentTitle"],
            Filter = loc["DialogSelectTorrentFilter"],
            Multiselect = true
        };

        if (dialog.ShowDialog() != true)
            return;

        await HandleDroppedFilesAsync(dialog.FileNames);
    }

    public Task HandleDroppedFilesAsync(IEnumerable<string> paths) =>
        ProcessExternalInputsAsync(paths);

    public Task HandleActivationArgsAsync(IEnumerable<string> args) =>
        ProcessExternalInputsAsync(args);

    private async Task ProcessExternalInputsAsync(IEnumerable<string> inputs)
    {
        var loc = LocalizationService.Instance;
        int added = 0;
        TorrentItemModel? lastAdded = null;
        bool lastWasMagnet = false;

        foreach (string raw in inputs)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            string cleaned = raw.Trim().Trim('"');
            if (cleaned.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                string? normalizedMagnet = FormatHelper.NormalizeToMagnetUri(cleaned);
                if (normalizedMagnet is null)
                {
                    ShowBanner(loc["BannerMagnetFailedTitle"], loc["ErrInvalidMagnetOrHash"], InfoBarSeverity.Error);
                    continue;
                }

                try
                {
                    lastAdded = await _engine.AddMagnetAsync(normalizedMagnet);
                    added++;
                    lastWasMagnet = true;
                }
                catch (Exception ex)
                {
                    ShowBanner(loc["BannerMagnetFailedTitle"], ex.Message, InfoBarSeverity.Error);
                }

                continue;
            }

            if (!cleaned.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                lastAdded = await _engine.AddTorrentFileAsync(cleaned);
                added++;
                lastWasMagnet = false;
            }
            catch (Exception ex)
            {
                ShowBanner(loc["BannerAddErrorTitle"], ex.Message, InfoBarSeverity.Error);
            }
        }

        if (added > 0)
        {
            if (CurrentSection is StoreNavSection.Home or StoreNavSection.Settings)
                CurrentSection = StoreNavSection.Downloads;
            SelectedTorrent = lastAdded;

            if (added == 1 && lastWasMagnet)
            {
                ShowBanner(
                    loc["BannerMagnetAddedTitle"],
                    loc.Format("BannerMagnetAddedBody", lastAdded?.Name),
                    InfoBarSeverity.Success);
            }
            else
            {
                ShowBanner(
                    loc["BannerTorrentAddedTitle"],
                    added == 1
                        ? loc.Format("BannerTorrentAddedSingle", lastAdded?.Name)
                        : loc.Format("BannerTorrentAddedMulti", added),
                    InfoBarSeverity.Success);
            }
        }
    }

    [RelayCommand]
    private void OpenAddMagnetModal()
    {
        MagnetSavePath = _settingsService.Current.DefaultDownloadPath;
        MagnetStartImmediately = _settingsService.Current.AutoStartOnAdd;

        try
        {
            if (Clipboard.ContainsText())
            {
                string clip = Clipboard.GetText().Trim();
                if (FormatHelper.NormalizeToMagnetUri(clip) is not null)
                    MagnetInputText = clip;
            }
        }
        catch { }

        IsAddMagnetModalOpen = true;
    }

    [RelayCommand]
    private void CloseAddMagnetModal()
    {
        IsAddMagnetModalOpen = false;
    }

    [RelayCommand]
    private void BrowseMagnetSavePath()
    {
        var folderDialog = new OpenFolderDialog
        {
            Title = LocalizationService.Instance["DialogSelectSaveFolderTitle"],
            InitialDirectory = Directory.Exists(MagnetSavePath) ? MagnetSavePath : _settingsService.Current.DefaultDownloadPath
        };

        if (folderDialog.ShowDialog() == true)
        {
            MagnetSavePath = folderDialog.FolderName;
        }
    }

    [RelayCommand]
    private async Task ConfirmAddMagnetAsync()
    {
        var loc = LocalizationService.Instance;
        string? normalized = FormatHelper.NormalizeToMagnetUri(MagnetInputText);
        if (normalized is null)
        {
            ShowBanner(loc["BannerSpecifyLinkTitle"], loc["BannerSpecifyLinkBody"], InfoBarSeverity.Warning);
            return;
        }

        string savePath = string.IsNullOrWhiteSpace(MagnetSavePath)
            ? _settingsService.Current.DefaultDownloadPath
            : MagnetSavePath.Trim();

        bool startNow = MagnetStartImmediately;
        IsAddMagnetModalOpen = false;
        MagnetInputText = string.Empty;

        await AddMagnetInternalAsync(normalized, savePath, startNow);
        CurrentSection = StoreNavSection.Downloads;
    }

    private async Task AddMagnetInternalAsync(string magnet, string savePath, bool startNow)
    {
        var loc = LocalizationService.Instance;
        try
        {
            var added = await _engine.AddMagnetAsync(magnet, savePath, startNow);
            SelectedTorrent = added;
            ShowBanner(loc["BannerMagnetAddedTitle"], loc.Format("BannerMagnetAddedBody", added.Name), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowBanner(loc["BannerMagnetFailedTitle"], ex.Message, InfoBarSeverity.Error);
        }
    }

    partial void OnCreateSourcePathChanged(string value)
    {
        CreateSourceSummary = BuildCreateSourceSummary(value);
    }

    internal static string BuildCreateSourceSummary(string? path)
    {
        var loc = LocalizationService.Instance;
        if (string.IsNullOrWhiteSpace(path))
            return loc["CreateSourceDefault"];

        string trimmed = path.Trim();
        try
        {
            if (File.Exists(trimmed))
            {
                long size = new FileInfo(trimmed).Length;
                int recPiece = BencodeHelper.ChooseRecommendedPieceLength(Math.Max(1, size));
                return loc.Format("CreateSourceFileFormat", FormatHelper.FormatBytes(size), FormatHelper.FormatBytes(recPiece));
            }

            if (Directory.Exists(trimmed))
            {
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
                if (string.IsNullOrWhiteSpace(Path.GetFileName(full)) || string.IsNullOrWhiteSpace(Path.GetDirectoryName(full)))
                    return loc["CreateSourceRootError"];

                int count = 0;
                long total = 0;
                foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    count++;
                    if (count <= 2000)
                        total += new FileInfo(file).Length;
                }

                if (count == 0)
                    return loc["CreateSourceEmptyFolder"];

                if (count > 2000)
                    return loc.Format("CreateSourceTooManyFiles", count);

                int recPiece = BencodeHelper.ChooseRecommendedPieceLength(Math.Max(1, total));
                return loc.Format("CreateSourceFolderFormat", count, FormatHelper.FormatBytes(total), FormatHelper.FormatBytes(recPiece));
            }
        }
        catch
        {
            return loc["CreateSourceReadError"];
        }

        return loc["CreateSourceNotFound"];
    }

    [RelayCommand]
    private void OpenCreateTorrentModal()
    {
        if (IsCreatingTorrent)
        {
            IsCreateTorrentModalOpen = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(CreateTrackersText) && _settingsService.Current.DefaultTrackers.Count > 0)
            CreateTrackersText = string.Join(Environment.NewLine, _settingsService.Current.DefaultTrackers);

        CreateTorrentProgress = 0;
        CreateSourceSummary = BuildCreateSourceSummary(CreateSourcePath);
        IsCreateTorrentModalOpen = true;
    }

    [RelayCommand]
    private void CloseCreateTorrentModal()
    {
        if (IsCreatingTorrent)
        {
            _createTorrentCts?.Cancel();
            return;
        }

        IsCreateTorrentModalOpen = false;
    }

    [RelayCommand]
    private void BrowseCreateSourceFile()
    {
        var loc = LocalizationService.Instance;
        var dialog = new OpenFileDialog
        {
            Title = loc["DialogSelectSourceFileTitle"],
            Filter = loc["DialogAllFilesFilter"]
        };

        if (dialog.ShowDialog() == true)
            CreateSourcePath = dialog.FileName;
    }

    [RelayCommand]
    private void BrowseCreateSourceFolder()
    {
        var folderDialog = new OpenFolderDialog
        {
            Title = LocalizationService.Instance["DialogSelectSourceFolderTitle"],
            InitialDirectory = Directory.Exists(CreateSourcePath)
                ? CreateSourcePath
                : _settingsService.Current.DefaultDownloadPath
        };

        if (folderDialog.ShowDialog() == true)
            CreateSourcePath = folderDialog.FolderName;
    }

    [RelayCommand]
    private async Task ConfirmCreateTorrentAsync()
    {
        if (IsCreatingTorrent)
            return;

        var loc = LocalizationService.Instance;
        string source = CreateSourcePath.Trim();
        if (string.IsNullOrWhiteSpace(source) || (!File.Exists(source) && !Directory.Exists(source)))
        {
            ShowBanner(loc["BannerSpecifySourceTitle"], loc["BannerSpecifySourceBody"], InfoBarSeverity.Warning);
            return;
        }

        string normalizedSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        string baseName = Path.GetFileName(normalizedSource);
        if (string.IsNullOrWhiteSpace(baseName) || string.IsNullOrWhiteSpace(Path.GetDirectoryName(normalizedSource)))
        {
            ShowBanner(loc["BannerInvalidPathTitle"], loc["BannerInvalidPathBody"], InfoBarSeverity.Warning);
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Title = loc["DialogSaveTorrentTitle"],
            Filter = loc["DialogTorrentFileFilter"],
            FileName = $"{baseName}.torrent",
            InitialDirectory = Path.GetDirectoryName(normalizedSource) ?? _settingsService.Current.DefaultDownloadPath
        };

        if (saveDialog.ShowDialog() != true)
            return;

        string outputTorrentPath = saveDialog.FileName;
        var trackers = (CreateTrackersText ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        int pieceSizeBytes = CreateSelectedPieceSizeIndex >= 0 && CreateSelectedPieceSizeIndex < CreatePieceSizeOptions.Count
            ? CreatePieceSizeOptions[CreateSelectedPieceSizeIndex].Bytes
            : 0;

        var options = new TorrentCreationOptions(
            normalizedSource,
            trackers,
            pieceSizeBytes,
            CreateIsPrivate,
            CreateComment);

        _createTorrentCts?.Dispose();
        _createTorrentCts = new CancellationTokenSource();
        var ct = _createTorrentCts.Token;

        IsCreatingTorrent = true;
        CreateTorrentProgress = 0;

        try
        {
            var progress = new Progress<double>(p => CreateTorrentProgress = p);
            TorrentCreationResult result = await Task.Run(
                () => BencodeHelper.CreateTorrentAsync(options, progress, ct),
                ct);

            await File.WriteAllBytesAsync(outputTorrentPath, result.TorrentBytes, ct);

            IsCreatingTorrent = false;
            IsCreateTorrentModalOpen = false;

            if (CreateStartSeeding)
            {
                var added = await _engine.AddTorrentFileAsync(outputTorrentPath, result.SeedSavePath, startImmediately: true);
                SelectedTorrent = added;
                CurrentSection = StoreNavSection.Active;
                ShowBanner(loc["BannerSeedCreatedTitle"], loc.Format("BannerSeedCreatedBody", Path.GetFileName(outputTorrentPath)), InfoBarSeverity.Success);
            }
            else
            {
                ShowBanner(loc["BannerTorrentCreatedTitle"], loc.Format("BannerTorrentCreatedBody", outputTorrentPath), InfoBarSeverity.Success);
            }
        }
        catch (OperationCanceledException)
        {
            ShowBanner(loc["BannerCreationCancelledTitle"], loc["BannerCreationCancelledBody"], InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowBanner(loc["BannerCreationErrorTitle"], ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsCreatingTorrent = false;
            _createTorrentCts?.Dispose();
            _createTorrentCts = null;
        }
    }

    [RelayCommand]
    private async Task StartTorrentAsync(TorrentItemModel? item)
    {
        if (item is null) return;
        await _engine.StartAsync(item);
        RebuildFilteredViews();
    }

    [RelayCommand]
    private async Task PauseTorrentAsync(TorrentItemModel? item)
    {
        if (item is null) return;
        await _engine.PauseAsync(item);
        TotalDownloadSpeed = _engine.TotalDownloadSpeed;
        TotalUploadSpeed = _engine.TotalUploadSpeed;
        RebuildFilteredViews();
    }

    [RelayCommand]
    private async Task StopTorrentAsync(TorrentItemModel? item)
    {
        if (item is null) return;
        await _engine.StopAsync(item);
        TotalDownloadSpeed = _engine.TotalDownloadSpeed;
        TotalUploadSpeed = _engine.TotalUploadSpeed;
        RebuildFilteredViews();
    }

    [RelayCommand]
    private async Task ForceRecheckAsync(TorrentItemModel? item)
    {
        if (item is null) return;
        await _engine.ForceRecheckAsync(item);
        var loc = LocalizationService.Instance;
        ShowBanner(loc["BannerRecheckTitle"], loc.Format("BannerRecheckBody", item.Name), InfoBarSeverity.Informational);
    }

    [RelayCommand]
    private async Task StartSelectedTorrentsAsync()
    {
        var targets = SelectedTorrents.Where(t => t.CanStart).ToList();
        if (targets.Count == 0)
            return;

        foreach (var target in targets)
            await _engine.StartAsync(target);

        RebuildFilteredViews();
    }

    [RelayCommand]
    private async Task PauseSelectedTorrentsAsync()
    {
        var targets = SelectedTorrents.Where(t => t.CanPause).ToList();
        if (targets.Count == 0)
            return;

        foreach (var target in targets)
            await _engine.PauseAsync(target);

        TotalDownloadSpeed = _engine.TotalDownloadSpeed;
        TotalUploadSpeed = _engine.TotalUploadSpeed;
        RebuildFilteredViews();
    }

    [RelayCommand]
    private async Task StopSelectedTorrentsAsync()
    {
        var targets = SelectedTorrents.Where(t => t.CanStop).ToList();
        if (targets.Count == 0)
            return;

        foreach (var target in targets)
            await _engine.StopAsync(target);

        TotalDownloadSpeed = _engine.TotalDownloadSpeed;
        TotalUploadSpeed = _engine.TotalUploadSpeed;
        RebuildFilteredViews();
    }

    [RelayCommand]
    private async Task StopOrPauseSelectedTorrentsAsync()
    {
        var targets = SelectedTorrents.Where(t => t.CanPause || t.CanStop).ToList();
        if (targets.Count == 0)
            return;

        foreach (var target in targets)
        {
            if (target.CanStop)
                await _engine.StopAsync(target);
            else
                await _engine.PauseAsync(target);
        }

        TotalDownloadSpeed = _engine.TotalDownloadSpeed;
        TotalUploadSpeed = _engine.TotalUploadSpeed;
        RebuildFilteredViews();
    }

    [RelayCommand]
    private void CopySelectedTorrentField(string? field)
    {
        if (SelectedTorrents.Count == 0 || string.IsNullOrWhiteSpace(field))
            return;

        string? text = field switch
        {
            "Name" => string.Join(Environment.NewLine, SelectedTorrents.Select(t => t.Name).Where(s => !string.IsNullOrWhiteSpace(s))),
            "InfoHash" => string.Join(Environment.NewLine, SelectedTorrents.Select(t => t.InfoHash).Where(s => !string.IsNullOrWhiteSpace(s))),
            "SavePath" => string.Join(
                Environment.NewLine,
                SelectedTorrents.Select(t => t.SavePath).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase)),
            "Trackers" => string.Join(
                Environment.NewLine,
                SelectedTorrents
                    .SelectMany(t => t.Trackers.Select(tr => tr.Uri).Concat(t.CustomTrackers))
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Distinct(StringComparer.OrdinalIgnoreCase)),
            "Magnet" => string.Join(
                Environment.NewLine,
                SelectedTorrents.Select(t => !string.IsNullOrWhiteSpace(t.MagnetUri) ? t.MagnetUri : $"magnet:?xt=urn:btih:{t.InfoHash}")),
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(text))
            (ClipboardTextWriter ?? Clipboard.SetText)(text);
    }

    private bool AreAllSelectedDownloadsUsingPostAction(PostCompletionAction action)
    {
        var downloads = SelectedTorrents.Where(t => !t.IsCompleted).ToList();
        return downloads.Count > 0 && downloads.All(t => t.PostCompletionAction == action);
    }

    private void SetSelectedDownloadsSeedAfterDownload(bool enabled)
    {
        var downloads = SelectedTorrents.Where(t => !t.IsCompleted).ToList();
        if (downloads.Count == 0)
            return;

        bool globalDefault = _settingsService.Current.SeedAfterDownload;
        bool? nextOverride = enabled == globalDefault ? null : enabled;
        foreach (var item in downloads)
            _engine.SetSeedAfterDownloadOverride(item, nextOverride);

        RefreshSelectionProperties();
    }

    private void SetSelectedDownloadsPostAction(PostCompletionAction action)
    {
        var downloads = SelectedTorrents.Where(t => !t.IsCompleted).ToList();
        if (downloads.Count == 0)
            return;

        foreach (var item in downloads)
            _engine.SetPostCompletionAction(item, action);

        RefreshSelectionProperties();
    }

    [RelayCommand]
    private void ToggleSelectedDownloadsSeed()
    {
        SetSelectedDownloadsSeedAfterDownload(!AreSelectedDownloadsSeedEnabled);
    }

    [RelayCommand]
    private void ToggleSelectedDownloadsPostAction(PostCompletionAction action)
    {
        PostCompletionAction next = AreAllSelectedDownloadsUsingPostAction(action)
            ? PostCompletionAction.None
            : action;
        SetSelectedDownloadsPostAction(next);
    }

    [RelayCommand]
    private void OpenSelectedTorrentDetails()
    {
        if (SelectedTorrents.Count == 1)
            OpenTorrentDetails(SelectedTorrents[0]);
    }

    [RelayCommand]
    private void RequestRemoveTorrent(TorrentItemModel? item)
    {
        if (item is null) return;
        OpenRemoveModalForTargets([item]);
    }

    [RelayCommand]
    private void RequestRemoveSelectedTorrents()
    {
        if (SelectedTorrents.Count == 0)
            return;

        OpenRemoveModalForTargets(SelectedTorrents);
    }

    private void OpenRemoveModalForTargets(IReadOnlyList<TorrentItemModel> targets)
    {
        var loc = LocalizationService.Instance;
        _torrentsPendingRemoval = targets.ToList();
        TorrentPendingRemoval = _torrentsPendingRemoval[0];
        RemoveModalTitle = _torrentsPendingRemoval.Count > 1
            ? loc.Format("RemoveModalTitleMulti", _torrentsPendingRemoval.Count)
            : loc["RemoveModalTitleSingle"];

        if (_torrentsPendingRemoval.Count == 1)
        {
            RemoveModalSummary = _torrentsPendingRemoval[0].Name;
        }
        else
        {
            var previewNames = _torrentsPendingRemoval.Take(5).Select(t => $"• {t.Name}").ToList();
            if (_torrentsPendingRemoval.Count > 5)
                previewNames.Add(loc.Format("RemoveModalMoreItems", _torrentsPendingRemoval.Count - 5));
            RemoveModalSummary = string.Join(Environment.NewLine, previewNames);
        }

        DeleteFilesOnRemove = false;
        IsRemoveModalOpen = true;
    }

    [RelayCommand]
    private void CancelRemoveTorrent()
    {
        IsRemoveModalOpen = false;
        TorrentPendingRemoval = null;
        _torrentsPendingRemoval.Clear();
    }

    [RelayCommand]
    private async Task ConfirmRemoveTorrentAsync()
    {
        if (_torrentsPendingRemoval.Count == 0 && TorrentPendingRemoval is null)
            return;

        var targets = _torrentsPendingRemoval.Count > 0
            ? _torrentsPendingRemoval.ToList()
            : [TorrentPendingRemoval!];
        bool deleteFiles = DeleteFilesOnRemove;

        IsRemoveModalOpen = false;
        TorrentPendingRemoval = null;
        _torrentsPendingRemoval.Clear();

        foreach (var target in targets)
        {
            if (SelectedTorrent == target)
                SelectedTorrent = null;

            SelectedTorrents.Remove(target);
            await _engine.RemoveAsync(target, deleteFiles);
        }

        RefreshSelectionProperties();
        RebuildFilteredViews();

        var loc = LocalizationService.Instance;
        string bannerTitle = targets.Count > 1 ? loc["BannerTorrentsRemovedTitle"] : loc["BannerTorrentRemovedTitle"];
        string bannerMsg = targets.Count > 1
            ? (deleteFiles
                ? loc.Format("BannerMultiRemovedWithFiles", targets.Count)
                : loc.Format("BannerMultiRemovedKeepFiles", targets.Count))
            : (deleteFiles
                ? loc.Format("BannerSingleRemovedWithFiles", targets[0].Name)
                : loc.Format("BannerSingleRemovedKeepFiles", targets[0].Name));
        ShowBanner(bannerTitle, bannerMsg, InfoBarSeverity.Informational);
    }

    [RelayCommand]
    private void OpenTorrentFolder(TorrentItemModel? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.SavePath))
            return;

        try
        {
            string target = item.SavePath;
            string subPath = Path.Combine(item.SavePath, item.Name);
            if (Directory.Exists(subPath))
                target = subPath;
            else if (File.Exists(subPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{subPath}\"") { UseShellExecute = true });
                return;
            }

            if (!Directory.Exists(target))
                Directory.CreateDirectory(target);

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            var loc = LocalizationService.Instance;
            ShowBanner(loc["BannerErrorTitle"], loc.Format("BannerOpenFolderFailed", ex.Message), InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private void CopyMagnetLink(TorrentItemModel? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.MagnetUri))
            return;

        try
        {
            Clipboard.SetText(item.MagnetUri);
            var loc = LocalizationService.Instance;
            ShowBanner(loc["BannerCopiedTitle"], loc["BannerCopiedMagnetBody"], InfoBarSeverity.Success);
        }
        catch { }
    }

    internal static StoreNavSection ResolveSectionForTorrent(TorrentItemModel item)
    {
        if (item.StatusKind == TorrentStatusKind.Seeding)
            return StoreNavSection.Seeding;

        if (item.IsCompleted)
            return StoreNavSection.Library;

        return StoreNavSection.Downloads;
    }

    [RelayCommand]
    private void OpenTorrentDetails(TorrentItemModel? item)
    {
        if (item is null)
            return;

        if (!IsTorrentsView)
        {
            var targetSection = ResolveSectionForTorrent(item);
            if (targetSection == StoreNavSection.Downloads)
                SelectedFilter = TorrentFilterKind.All;
            CurrentSection = targetSection;
        }

        SelectedTorrent = item;
        IsDetailsOpen = true;
    }

    [RelayCommand]
    private void CloseDetails()
    {
        SelectedTorrent = null;
        IsDetailsOpen = false;
        RebuildFilteredViews();
    }

    [RelayCommand]
    private async Task ReannounceTorrentAsync(TorrentItemModel? item)
    {
        if (item is null) return;
        await _engine.ReannounceAsync(item);
        var loc = LocalizationService.Instance;
        ShowBanner(loc["BannerReannounceTitle"], loc.Format("BannerReannounceBody", item.Name), InfoBarSeverity.Informational);
    }

    [RelayCommand]
    private async Task SelectAllFilesAsync()
    {
        if (SelectedTorrent is null) return;
        await _engine.SetAllFilesPriorityAsync(SelectedTorrent, TorrentFilePriority.Normal);
    }

    [RelayCommand]
    private async Task DeselectAllFilesAsync()
    {
        if (SelectedTorrent is null) return;
        await _engine.SetAllFilesPriorityAsync(SelectedTorrent, TorrentFilePriority.DoNotDownload);
    }

    [RelayCommand]
    private async Task AddTrackerToSelectedAsync()
    {
        if (SelectedTorrent is null)
            return;

        var loc = LocalizationService.Instance;
        if (string.IsNullOrWhiteSpace(NewTrackerInputText))
        {
            ShowBanner(loc["BannerSpecifyTrackerTitle"], loc["BannerSpecifyTrackerBody"], InfoBarSeverity.Warning);
            return;
        }

        string url = NewTrackerInputText.Trim();
        try
        {
            await _engine.AddTrackerAsync(SelectedTorrent, url);
            NewTrackerInputText = string.Empty;
        }
        catch (Exception ex)
        {
            ShowBanner(loc["BannerAddTrackerFailedTitle"], ex.Message, InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task RemoveTrackerFromSelectedAsync(string? trackerUri)
    {
        if (SelectedTorrent is null || string.IsNullOrWhiteSpace(trackerUri))
            return;

        await _engine.RemoveTrackerAsync(SelectedTorrent, trackerUri);
    }

    public async Task ToggleFileSelectedAsync(TorrentFileModel file, bool isSelected)
    {
        if (SelectedTorrent is null) return;
        var targetPriority = isSelected ? TorrentFilePriority.Normal : TorrentFilePriority.DoNotDownload;
        if (file.Priority != targetPriority)
        {
            await _engine.SetFilePriorityAsync(SelectedTorrent, file, targetPriority);
        }
    }

    [RelayCommand]
    private async Task PauseAllAsync()
    {
        foreach (var t in _engine.Torrents.Where(x => x.CanPause || x.CanStop).ToList())
        {
            if (t.CanStop)
                await _engine.StopAsync(t);
            else
                await _engine.PauseAsync(t);
        }
        TotalDownloadSpeed = _engine.TotalDownloadSpeed;
        TotalUploadSpeed = _engine.TotalUploadSpeed;
        RebuildFilteredViews();
    }

    [RelayCommand]
    private async Task StartAllAsync()
    {
        foreach (var t in _engine.Torrents.Where(x => x.CanStart).ToList())
            await _engine.StartAsync(t);
        RebuildFilteredViews();
    }

    [RelayCommand]
    private void BrowseDefaultDownloadPath()
    {
        var folderDialog = new OpenFolderDialog
        {
            Title = LocalizationService.Instance["DialogDefaultDownloadsTitle"],
            InitialDirectory = Directory.Exists(SettingsDownloadPath)
                ? SettingsDownloadPath
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (folderDialog.ShowDialog() == true)
        {
            SettingsDownloadPath = folderDialog.FolderName;
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        string resolvedLanguage = LocalizationService.ResolveSupportedLanguageCode(SettingsLanguage);
        var updated = new AppSettings
        {
            DefaultDownloadPath = string.IsNullOrWhiteSpace(SettingsDownloadPath)
                ? _settingsService.Current.DefaultDownloadPath
                : SettingsDownloadPath.Trim(),
            ThemeMode = SettingsThemeMode,
            ColorTheme = SettingsColorTheme,
            Language = resolvedLanguage,
            ListenPort = Math.Clamp(SettingsListenPort, 1024, 65535),
            EnableDht = SettingsEnableDht,
            EnableLocalPeerDiscovery = SettingsEnableLpd,
            EnablePortForwarding = SettingsEnablePortForwarding,
            MaxDownloadSpeedKBps = Math.Max(0, SettingsMaxDownloadKBps),
            MaxUploadSpeedKBps = Math.Max(0, SettingsMaxUploadKBps),
            MaxConnections = Math.Clamp(SettingsMaxConnections, 10, 2000),
            ReannounceIntervalSeconds = SettingsReannounceIntervalSeconds <= 0 ? 0 : Math.Clamp(SettingsReannounceIntervalSeconds, 10, 86400),
            DefaultTrackers = FormatHelper.ParseTrackerList(SettingsDefaultTrackersText),
            AutoStartOnAdd = SettingsAutoStart,
            SeedAfterDownload = SettingsSeedAfterDownload,
            MinimizeToTrayOnClose = SettingsMinimizeToTrayOnClose,
            StartMinimized = SettingsStartMinimized,
            LaunchAtStartup = SettingsLaunchAtStartup,
            SortKind = SelectedSort,
            UpdateRepository = GitHubUpdateService.TryNormalizeRepository(SettingsUpdateRepository) ?? SettingsUpdateRepository.Trim(),
            AutoCheckUpdatesOnStartup = SettingsAutoCheckUpdates
        };

        _startupService?.SetStartupEnabled(updated.LaunchAtStartup);
        _settingsService.SaveSettings(updated);
        await _engine.ApplySettingsAsync(updated);
        RefreshSelectionProperties();
        LocalizationService.Instance.SetLanguage(updated.Language);

        void ApplyThemeChange()
        {
            _themeService.ApplyTheme(updated.ThemeMode, updated.ColorTheme, Application.Current?.MainWindow);
            IsDarkTheme = _themeService.IsDarkActive;
            NotifyThemeBrushesRefreshed();
        }

        if (ThemeTransitionRunner is not null)
            ThemeTransitionRunner(ApplyThemeChange);
        else
            ApplyThemeChange();

        var loc = LocalizationService.Instance;
        ShowBanner(loc["BannerSettingsSavedTitle"], loc["BannerSettingsSavedBody"], InfoBarSeverity.Success);
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        await CheckForUpdatesInternalAsync(SettingsUpdateRepository, notifyIfUpToDateOrError: true);
    }

    private async Task CheckForUpdatesInternalAsync(string repository, bool notifyIfUpToDateOrError)
    {
        if (_updateService is null || IsCheckingForUpdates)
            return;

        var loc = LocalizationService.Instance;
        IsCheckingForUpdates = true;
        UpdateStatusText = loc["UpdateStatusChecking"];

        try
        {
            var result = await _updateService.CheckForUpdatesAsync(repository);
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                AvailableUpdate = null;
                IsUpdateBannerVisible = false;
                UpdateStatusText = loc.Format("UpdateStatusCheckError", result.ErrorMessage);
                if (notifyIfUpToDateOrError)
                    ShowBanner(loc["UpdateBannerTitle"], result.ErrorMessage, InfoBarSeverity.Warning);
                return;
            }

            if (result.IsUpdateAvailable && result.LatestRelease is not null)
            {
                AvailableUpdate = result.LatestRelease;
                IsUpdateBannerVisible = true;
                UpdateStatusText = loc.Format("UpdateStatusAvailable", result.LatestRelease.TagName, CurrentVersionText);
                ShowBanner(
                    loc["UpdateAvailableTitle"],
                    loc.Format("UpdateAvailableBody", result.LatestRelease.TagName),
                    InfoBarSeverity.Informational);
            }
            else
            {
                AvailableUpdate = null;
                IsUpdateBannerVisible = false;
                UpdateStatusText = loc.Format("UpdateStatusUpToDate", CurrentVersionText);
                if (notifyIfUpToDateOrError)
                    ShowBanner(loc["UpdateNotNeededTitle"], loc.Format("UpdateNotNeededBody", CurrentVersionText), InfoBarSeverity.Success);
            }
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAndInstallUpdateAsync()
    {
        if (_updateService is null || AvailableUpdate is null || IsDownloadingUpdate)
            return;

        if (AvailableUpdate.Asset is null)
        {
            OpenUpdateReleasePage();
            return;
        }

        var loc = LocalizationService.Instance;
        IsDownloadingUpdate = true;
        UpdateDownloadProgress = 0;
        UpdateStatusText = loc.Format("UpdateStatusDownloading", AvailableUpdate.Asset.Name);

        try
        {
            var progress = new Progress<double>(pct =>
            {
                UpdateDownloadProgress = pct;
                UpdateStatusText = loc.Format("UpdateStatusDownloadProgress", AvailableUpdate.Asset.Name, $"{pct:0}");
            });

            string preparedPath = await _updateService.DownloadAndPrepareUpdateAsync(AvailableUpdate, progress);
            UpdateStatusText = loc["UpdateStatusInstalling"];
            await ShutdownAsync();
            _updateService.LaunchPreparedUpdateAndExit(preparedPath);
        }
        catch (Exception ex)
        {
            UpdateStatusText = loc.Format("UpdateStatusInstallFailed", ex.Message);
            ShowBanner(loc["UpdateErrorTitle"], ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    [RelayCommand]
    private void OpenUpdateReleasePage()
    {
        if (AvailableUpdate is null || string.IsNullOrWhiteSpace(AvailableUpdate.HtmlUrl))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(AvailableUpdate.HtmlUrl) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    [RelayCommand]
    private void DismissUpdateBanner()
    {
        IsUpdateBannerVisible = false;
    }

    [RelayCommand]
    private void OpenUpdatesSettings()
    {
        CurrentSection = StoreNavSection.Settings;
    }

    public async Task ChangeFilePriorityAsync(TorrentFileModel file, TorrentFilePriority newPriority)
    {
        if (SelectedTorrent is null)
            return;

        await _engine.SetFilePriorityAsync(SelectedTorrent, file, newPriority);
    }

    private void ShowBanner(string title, string message, InfoBarSeverity severity)
    {
        var kind = severity switch
        {
            InfoBarSeverity.Success => NotificationKind.Success,
            InfoBarSeverity.Warning => NotificationKind.Warning,
            InfoBarSeverity.Error => NotificationKind.Error,
            _ => NotificationKind.Info
        };

        _notifications?.Show(title, message, kind);
    }
}

public sealed partial class ColorThemeSwatchOption : ObservableObject
{
    public required AppColorTheme Theme { get; init; }
    public required Brush SwatchBrush { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}
