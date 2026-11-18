using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Gem.Helpers;
using Gem.Models;
using TorrentSharp.Wrap;
using TorrentSharp.Wrap.Configurations;
using TorrentSharp.Wrap.Configurations.Options;
using TorrentSharp.Wrap.Configurations.Settings;
using TorrentSharp.Wrap.Enums;
using TorrentSharp.Wrap.Notifications;
using TorrentSharp.Wrap.Utils;

namespace Gem.Services;

/// <summary>
/// libtorrent-rasterbar (via TorrentSharp.Wrap) BitTorrent engine service with uTP, DHT, sparse storage,
/// custom BitTorrent 1.0.0 client identity, zero-second stop/removal timeout, and fast resume persistence.
/// </summary>
public sealed class LibtorrentEngineService : ITorrentEngineService
{
    private static readonly string[] DefaultDhtRouters =
    [
        "router.bittorrent.com:6881",
        "router.utorrent.com:6881",
        "dht.transmissionbt.com:6881",
        "dht.libtorrent.org:25401"
    ];

    private static readonly TimeSpan UploadingPieceVisibilityWindow = TimeSpan.FromSeconds(2.5);
    private static readonly FieldInfo? TorrentManagerHandleField =
        typeof(TorrentManager).GetField("TorrentSessionHandle", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly SettingsService _settingsService;
    private readonly Dictionary<string, TorrentManager> _managers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pausedHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _savePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int PieceLength, bool IsPrivate)> _metadataHints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Down, long Up)> _lastManagerBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastForcedReannounceUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingRemovals = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _uploadTrackLock = new();
    private readonly Dictionary<string, HashSet<string>> _torrentNameToHashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _registeredNamesByHash = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<int, DateTime>> _recentUploadingPieces = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _checkQueueLock = new();
    private readonly Queue<PendingCheckEntry> _pendingCheckQueue = new();
    private readonly HashSet<string> _queuedCheckHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _keepPausedAfterCheck = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeCheckingHash;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _checkPumpTimer;

    private readonly Dictionary<string, (long Down, long Up, TorrentState State)> _lastSavedResumeState = new(StringComparer.OrdinalIgnoreCase);
    private TorrentClient? _client;
    private TrafficStatsData _trafficStats = new();
    private bool _trafficStatsDirty;
    private int _trafficSaveTickCounter;
    private int _resumeSaveTickCounter;
    private int _dhtPollTickCounter;
    private bool _isRefreshingTick;
    private bool _isSavingResumeData;
    private bool _isRefreshingDhtNodes;
    private bool _disposed;

    public ObservableCollection<TorrentItemModel> Torrents { get; } = new();

    public long TotalDownloadSpeed { get; private set; }

    public long TotalUploadSpeed { get; private set; }

    public long AllTimeDownloadedBytes => _trafficStats.TotalDownloadedBytes;

    public long AllTimeUploadedBytes => _trafficStats.TotalUploadedBytes;

    public IReadOnlyList<DailyTrafficPoint> DailyTrafficHistory { get; private set; } = [];

    public int DhtNodesCount { get; private set; }

    public bool IsRunning { get; private set; }

    public event Action? StatsUpdated;

    public LibtorrentEngineService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAllTickAsync();

        _checkPumpTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _checkPumpTimer.Tick += (_, _) => AdvanceCheckQueue();
    }

    private string SessionStateFilePath => Path.Combine(_settingsService.CacheDirectory, "libtorrent_session.state");

    private string FastResumeDirectory => Path.Combine(_settingsService.CacheDirectory, "fastresume");

    public async Task InitializeAsync()
    {
        if (_client is not null)
            return;

        Directory.CreateDirectory(FastResumeDirectory);
        var settings = _settingsService.LoadSettings();
        Directory.CreateDirectory(settings.DefaultDownloadPath);

        byte[]? savedSessionState = null;
        if (File.Exists(SessionStateFilePath))
        {
            try { savedSessionState = await File.ReadAllBytesAsync(SessionStateFilePath); } catch { }
        }

        _client = new TorrentClient(opts =>
        {
            if (savedSessionState is { Length: > 0 })
            {
                opts.State(savedSessionState);
            }

            opts.OnNotification(OnSessionNotification);
            opts.Notifications(ConfigureNotifications);

            opts.Identity(id =>
            {
                id.UserAgent("Gem/1.0.0");
                id.HandshakeClientVersion("BitTorrent 1.0.0");
                id.PeerFingerprint("-BT1000-");
            });

            opts.Network(n => ConfigureNetworkOptions(n, settings.ListenPort));

            opts.Discovery(d =>
            {
                d.Dht(settings.EnableDht, DefaultDhtRouters);
                d.Lsd(settings.EnableLocalPeerDiscovery);
                d.Upnp(settings.EnablePortForwarding);
                d.NatPmp(settings.EnablePortForwarding);
            });

            opts.Limits(l =>
            {
                l.Download(Math.Max(0, settings.MaxDownloadSpeedKBps) * 1024);
                l.Upload(Math.Max(0, settings.MaxUploadSpeedKBps) * 1024);
                l.Connections(Math.Clamp(settings.MaxConnections, 10, 2000));
                l.SeedingOutgoingConnections(true);
            });

            opts.Storage(s => s.DefaultDownloadPath(settings.DefaultDownloadPath));
            opts.Advanced(sp => ApplyAdvancedSettingsPack(sp, settings));
        });

        IsRunning = true;
        _trafficStats = _settingsService.LoadTrafficStats();

        var savedEntries = _settingsService.LoadSession();
        foreach (var entry in savedEntries)
        {
            try
            {
                await RestoreEntryAsync(entry);
            }
            catch
            {
                // Keep loading remaining session entries even if one is corrupted.
            }
        }

        if (_trafficStats.TotalDownloadedBytes == 0 && _trafficStats.TotalUploadedBytes == 0 && Torrents.Count > 0)
        {
            long initialDown = Torrents.Sum(t => t.DownloadedBytes);
            long initialUp = Torrents.Sum(t => t.UploadedBytes);
            if (initialDown > 0 || initialUp > 0)
            {
                RecordTrafficDelta(_trafficStats, initialDown, initialUp, DateTime.Now);
                _settingsService.SaveTrafficStats(_trafficStats);
            }
        }

        DailyTrafficHistory = BuildRecentDaysWindow(_trafficStats, DateTime.Now, 14);

        _refreshTimer.Start();
        LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
        StatsUpdated?.Invoke();
    }

    private static void ConfigureNotifications(NotificationOptions notifications)
    {
        notifications
            .Categories(
                NotificationCategories.Error |
                NotificationCategories.Status |
                NotificationCategories.Storage |
                NotificationCategories.Upload)
            .IncludeUnmapped(true);
    }

    private static void ConfigureNetworkOptions(NetworkOptions network, int listenPort)
    {
        ListenInterface[] ifaces = BuildRoutableListenInterfaces(listenPort);
        if (ifaces.Length > 0)
            network.ListenOn(ifaces);
        else
            network.ListenOn(IPAddress.Any, listenPort, ListenFlags.None);

        network.Tcp(true, true);
        network.Utp(true, true);
    }

    public static ListenInterface[] BuildRoutableListenInterfaces(int listenPort)
    {
        try
        {
            var upNics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                             ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            var withGateway = upNics
                .Where(ni => ni.GetIPProperties().GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.Any.Equals(g.Address)))
                .ToList();

            var sourceNics = withGateway.Count > 0 ? withGateway : upNics;

            return sourceNics
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Select(ua => ua.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork &&
                             !IPAddress.IsLoopback(ip) &&
                             !IsLinkLocalIPv4(ip))
                .Distinct()
                .Select(ip => (ListenInterface)new IpInterface(new IPEndPoint(ip, listenPort), ListenFlags.None))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static bool IsLinkLocalIPv4(IPAddress ip)
    {
        Span<byte> bytes = stackalloc byte[4];
        return ip.TryWriteBytes(bytes, out int written) &&
               written == 4 &&
               bytes[0] == 169 &&
               bytes[1] == 254;
    }

    private static void ApplyAdvancedSettingsPack(SettingsPack sp, AppSettings settings)
    {
        sp.Set(new StopTrackerTimeout(0));
        sp.Set(new ConnectionSpeed(40));
        sp.Set(new PeerConnectTimeout(5));
        sp.Set(new RequestTimeout(15));
        sp.Set(new AnnounceToAllTrackers(true));
        sp.Set(new AnnounceToAllTiers(true));
        sp.Set(new SeedingOutgoingConnections(true));
        sp.Set(new ActiveChecking(1));
        sp.Set(new ActiveDownloads(5));
        sp.Set(new ActiveSeeds(25));
        sp.Set(new ActiveLimit(30));
        sp.Set(new EnableIncomingUtp(true));
        sp.Set(new EnableOutgoingUtp(true));
        sp.Set(new ConnectionsLimit(Math.Clamp(settings.MaxConnections, 10, 2000)));

        // Classic libtorrent 1.2.20 LRU disk cache (no mmap_disk_io); Windows OS cache handles bulk I/O.
        sp.Set(new CacheSizeSetting(1024)); // 1024 * 16 KiB = 16 MiB classic disk cache
        sp.Set(new CacheExpirySetting(60));
        sp.Set(new UseReadCacheSetting(true));
        sp.Set(new DiskIoReadMode(0));  // enable_os_cache on reads
        sp.Set(new DiskIoWriteMode(0)); // enable_os_cache on writes
        sp.Set(new NoAtimeStorage(true));
        sp.Set(new MaxQueuedDiskBytes(4 * 1024 * 1024));
        sp.Set(new CheckingMemUsage(32));
        sp.Set(new FilePoolSize(10));
        sp.Set(new CloseFileInterval(30));
        sp.Set(new AioThreads(2));
        sp.Set(new MaxConcurrentHttpAnnounces(8));
        sp.Set(new AlertQueueSize(2048));
    }

    private sealed record CacheSizeSetting(int Value) : ISettingsEntry<CacheSizeSetting>
    {
        public static string Key => "cache_size";
        object ISettingsEntry<CacheSizeSetting>.Value => Value;
        public static CacheSizeSetting FromValue(object value) => new(Convert.ToInt32(value));
    }

    private sealed record CacheExpirySetting(int Value) : ISettingsEntry<CacheExpirySetting>
    {
        public static string Key => "cache_expiry";
        object ISettingsEntry<CacheExpirySetting>.Value => Value;
        public static CacheExpirySetting FromValue(object value) => new(Convert.ToInt32(value));
    }

    private sealed record UseReadCacheSetting(bool Value) : ISettingsEntry<UseReadCacheSetting>
    {
        public static string Key => "use_read_cache";
        object ISettingsEntry<UseReadCacheSetting>.Value => Value;
        public static UseReadCacheSetting FromValue(object value) => new(Convert.ToBoolean(value));
    }

    private void OnSessionNotification(object? sender, SessionNotification notification)
    {
        if ((notification.Category & (int)NotificationCategories.Upload) != 0 &&
            TryParseBlockUploadedMessage(notification.Message, out string torrentName, out int pieceIndex))
        {
            RecordUploadedPiece(torrentName, pieceIndex, DateTime.UtcNow);
            return;
        }

        switch (notification)
        {
            case TorrentRemovedNotification removed:
            {
                string hash = removed.TorrentManager.InfoHash;
                if (!string.IsNullOrWhiteSpace(hash) &&
                    _pendingRemovals.TryGetValue(hash, out var tcs))
                {
                    tcs.TrySetResult(true);
                }
                break;
            }
            case MetadataReceivedNotification metadataReceived:
                HandleMetadataReceived(metadataReceived.TorrentManager);
                break;
            case TorrentErrorNotification torrentError:
                HandleTorrentError(torrentError.TorrentManager.InfoHash, LocalizationService.Instance.Format("ErrTorrentError", torrentError.ErrorValue));
                break;
            case FileErrorNotification fileError:
                HandleTorrentError(fileError.TorrentManager.InfoHash, LocalizationService.Instance.Format("ErrFileError", fileError.Filename, fileError.ErrorValue));
                break;
            case TorrentStatusNotification statusChanged:
                if (!IsCheckingState(statusChanged.NewState))
                    ScheduleCheckQueueAdvance();
                break;
        }
    }

    private void HandleMetadataReceived(TorrentManager manager)
    {
        string hash = NormalizeHash(manager.InfoHash);
        string storedTorrentPath = Path.Combine(_settingsService.TorrentsStoreDirectory, $"{hash}.torrent");
        try
        {
            manager.Info?.SaveToFile(storedTorrentPath);
        }
        catch
        {
            storedTorrentPath = string.Empty;
        }

        if (_settingsService.Current.DefaultTrackers.Count > 0)
        {
            ApplyDefaultTrackersToManager(manager, _settingsService.Current.DefaultTrackers);
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var model = Torrents.FirstOrDefault(t => string.Equals(t.InfoHash, hash, StringComparison.OrdinalIgnoreCase));
            if (model is null)
                return;

            if (!string.IsNullOrWhiteSpace(storedTorrentPath) && File.Exists(storedTorrentPath))
                model.TorrentFilePath = storedTorrentPath;

            RefreshMetadataHints(manager, hash);
            SyncModel(manager, model);
            SyncFiles(manager, model);
            SyncTrackers(manager, model);
            SaveSessionState();
        });
    }

    private void HandleTorrentError(string rawHash, string message)
    {
        string hash = NormalizeHash(rawHash);
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var model = Torrents.FirstOrDefault(t => string.Equals(t.InfoHash, hash, StringComparison.OrdinalIgnoreCase));
            if (model is not null)
                model.ErrorMessage = message;
        });
    }

    private async Task RestoreEntryAsync(SavedTorrentEntry entry)
    {
        if (_client is null)
            return;

        string savePath = string.IsNullOrWhiteSpace(entry.SavePath)
            ? _settingsService.Current.DefaultDownloadPath
            : entry.SavePath;
        Directory.CreateDirectory(savePath);

        byte[]? resumeBytes = LoadResumeBytes(entry.InfoHash);
        TorrentManager? manager = null;

        if (!string.IsNullOrWhiteSpace(entry.TorrentFilePath) && File.Exists(entry.TorrentFilePath))
        {
            var info = new TorrentInfo(entry.TorrentFilePath);
            string hash = NormalizeHash(info.Metadata.InfoHash);
            if (_managers.ContainsKey(hash))
                return;

            manager = _client.AttachTorrent(info, savePath, resumeBytes, downloadByDefault: true);
        }
        else if (!string.IsNullOrWhiteSpace(entry.MagnetUri))
        {
            string? normalized = FormatHelper.NormalizeToMagnetUri(entry.MagnetUri);
            if (normalized is null)
                return;

            string hash = NormalizeHash(entry.InfoHash);
            if (!string.IsNullOrEmpty(hash) && _managers.ContainsKey(hash))
                return;

            manager = _client.AttachMagnet(normalized, savePath, resumeBytes, downloadByDefault: true);
        }

        if (manager is null)
            return;

        string infoHash = NormalizeHash(manager.InfoHash);
        _savePaths[infoHash] = savePath;
        if (entry.WasPaused)
            _pausedHashes.Add(infoHash);

        var model = CreateModelFromManager(manager, savePath, entry.TorrentFilePath, entry.MagnetUri, entry.AddedAt);
        foreach (string custom in entry.CustomTrackers)
        {
            string? normalizedTracker = FormatHelper.TryNormalizeTrackerUri(custom);
            if (normalizedTracker is not null && !model.CustomTrackers.Contains(normalizedTracker, StringComparer.OrdinalIgnoreCase))
            {
                model.CustomTrackers.Add(normalizedTracker);
                AddTrackerUriIfMissing(manager, normalizedTracker);
            }
        }

        ApplyDefaultTrackersToManager(manager, _settingsService.Current.DefaultTrackers);
        SyncTrackers(manager, model);
        RegisterManager(manager, model);

        if (!entry.WasPaused)
        {
            StartOrQueueCheck(infoHash, manager, model, callForceRecheck: false, keepPausedAfterCheck: false);
        }
        else
        {
            manager.Stop();
            model.StatusKind = TorrentStatusKind.Paused;
            model.StatusText = LocalizationService.Instance["StatusPaused"];
        }

        SyncModel(manager, model);
        await Task.CompletedTask;
    }

    public async Task<TorrentItemModel> AddTorrentFileAsync(string torrentFilePath, string? savePath = null, bool? startImmediately = null)
    {
        EnsureInitialized();

        if (!File.Exists(torrentFilePath))
            throw new FileNotFoundException(LocalizationService.Instance["ErrTorrentFileNotFound"], torrentFilePath);

        var info = new TorrentInfo(torrentFilePath);
        string infoHashHex = NormalizeHash(info.Metadata.InfoHash);

        if (_managers.ContainsKey(infoHashHex))
        {
            var existing = Torrents.First(t => string.Equals(t.InfoHash, infoHashHex, StringComparison.OrdinalIgnoreCase));
            throw new InvalidOperationException(LocalizationService.Instance.Format("ErrTorrentAlreadyAdded", existing.Name));
        }

        string targetPath = string.IsNullOrWhiteSpace(savePath)
            ? _settingsService.Current.DefaultDownloadPath
            : savePath;
        Directory.CreateDirectory(targetPath);

        string storedTorrentPath = _settingsService.PersistTorrentFile(torrentFilePath, infoHashHex);
        var manager = _client!.AttachTorrent(info, targetPath, LoadResumeBytes(infoHashHex), downloadByDefault: true);
        _savePaths[infoHashHex] = targetPath;

        ApplyDefaultTrackersToManager(manager, _settingsService.Current.DefaultTrackers);
        var model = CreateModelFromManager(manager, targetPath, storedTorrentPath, BuildMagnetUri(manager), DateTime.UtcNow);
        RegisterManager(manager, model);

        bool autoStart = startImmediately ?? _settingsService.Current.AutoStartOnAdd;
        if (autoStart)
        {
            StartOrQueueCheck(infoHashHex, manager, model, callForceRecheck: false, keepPausedAfterCheck: false);
        }
        else
        {
            _pausedHashes.Add(infoHashHex);
            manager.Stop();
        }

        SyncModel(manager, model);
        SaveSessionState();
        return await Task.FromResult(model);
    }

    public async Task<TorrentItemModel> AddMagnetAsync(string magnetUri, string? savePath = null, bool? startImmediately = null)
    {
        EnsureInitialized();

        string? normalized = FormatHelper.NormalizeToMagnetUri(magnetUri);
        if (normalized is null)
            throw new FormatException(LocalizationService.Instance["ErrInvalidMagnetOrHash"]);

        string targetPath = string.IsNullOrWhiteSpace(savePath)
            ? _settingsService.Current.DefaultDownloadPath
            : savePath;
        Directory.CreateDirectory(targetPath);

        string? expectedHash = ExtractInfoHashFromMagnet(normalized);
        if (!string.IsNullOrEmpty(expectedHash) && _managers.ContainsKey(expectedHash))
        {
            var existing = Torrents.First(t => string.Equals(t.InfoHash, expectedHash, StringComparison.OrdinalIgnoreCase));
            throw new InvalidOperationException(LocalizationService.Instance.Format("ErrTorrentAlreadyAdded", existing.Name));
        }

        var manager = _client!.AttachMagnet(normalized, targetPath, expectedHash is not null ? LoadResumeBytes(expectedHash) : null, downloadByDefault: true);
        string infoHashHex = NormalizeHash(manager.InfoHash);
        _savePaths[infoHashHex] = targetPath;

        ApplyDefaultTrackersToManager(manager, _settingsService.Current.DefaultTrackers);
        var model = CreateModelFromManager(manager, targetPath, null, normalized, DateTime.UtcNow);
        RegisterManager(manager, model);

        bool autoStart = startImmediately ?? _settingsService.Current.AutoStartOnAdd;
        if (autoStart)
        {
            StartOrQueueCheck(infoHashHex, manager, model, callForceRecheck: false, keepPausedAfterCheck: false);
        }
        else
        {
            _pausedHashes.Add(infoHashHex);
            manager.Stop();
        }

        SyncModel(manager, model);
        SaveSessionState();
        return await Task.FromResult(model);
    }

    public Task StartAsync(TorrentItemModel item)
    {
        if (_managers.TryGetValue(item.InfoHash, out var manager))
        {
            string hash = NormalizeHash(item.InfoHash);
            bool needsSequentialCheck = IsQueuedForCheck(hash) || IsCheckingState(manager.GetCurrentStatus().State);
            if (needsSequentialCheck)
            {
                StartOrQueueCheck(hash, manager, item, callForceRecheck: false, keepPausedAfterCheck: false);
            }
            else
            {
                _pausedHashes.Remove(hash);
                manager.Start();
                if (IsCheckingState(manager.GetCurrentStatus().State) && IsCheckBusyForOther(hash))
                {
                    StartOrQueueCheck(hash, manager, item, callForceRecheck: false, keepPausedAfterCheck: false);
                }
                else
                {
                    SyncModel(manager, item);
                }
            }
            SaveSessionState();
        }

        return Task.CompletedTask;
    }

    public Task PauseAsync(TorrentItemModel item)
    {
        if (_managers.TryGetValue(item.InfoHash, out var manager))
        {
            CancelPendingCheck(item.InfoHash);
            _pausedHashes.Add(item.InfoHash);
            manager.Stop();
            SyncModel(manager, item);
            RecalculateTotalSpeeds();
            SaveSessionState();
            AdvanceCheckQueue();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(TorrentItemModel item) => PauseAsync(item);

    public Task ForceRecheckAsync(TorrentItemModel item)
    {
        if (_managers.TryGetValue(item.InfoHash, out var manager))
        {
            bool keepPausedAfter = _pausedHashes.Contains(item.InfoHash);
            StartOrQueueCheck(item.InfoHash, manager, item, callForceRecheck: true, keepPausedAfterCheck: keepPausedAfter);
        }

        return Task.CompletedTask;
    }

    public Task ReannounceAsync(TorrentItemModel item)
    {
        if (_managers.TryGetValue(item.InfoHash, out var manager))
        {
            _lastForcedReannounceUtc[item.InfoHash] = DateTime.UtcNow;
            manager.ReannounceAllTrackers(TimeSpan.Zero, true);
            PruneOfflineTrackers(manager, item);
            SyncTrackers(manager, item);
            SyncModel(manager, item);
        }

        return Task.CompletedTask;
    }

    public async Task RemoveAsync(TorrentItemModel item, bool deleteFiles)
    {
        if (_client is null)
            return;

        Torrents.Remove(item);
        CancelPendingCheck(item.InfoHash);
        _pausedHashes.Remove(item.InfoHash);
        _lastForcedReannounceUtc.Remove(item.InfoHash);
        _lastSavedResumeState.Remove(item.InfoHash);
        _metadataHints.Remove(item.InfoHash);
        _savePaths.Remove(item.InfoHash);
        UnregisterTorrentUploadTracking(item.InfoHash);
        RecalculateTotalSpeeds();
        SaveSessionState();
        StatsUpdated?.Invoke();
        AdvanceCheckQueue();

        if (_managers.TryGetValue(item.InfoHash, out var manager))
        {
            _managers.Remove(item.InfoHash);

            string baseSavePath = string.IsNullOrWhiteSpace(item.SavePath)
                ? _settingsService.Current.DefaultDownloadPath
                : item.SavePath;
            var filePaths = manager.Files
                .Select(f => Path.Combine(baseSavePath, f.Info.Path))
                .ToList();
            string? topSubDir = manager.Files.Count > 1
                ? ExtractTopSubDirectory(baseSavePath, manager.Files[0].Info.Path)
                : null;

            var removedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRemovals[manager.InfoHash] = removedTcs;

            try
            {
                manager.Stop();
                _client.DetachTorrent(manager);

                if (deleteFiles)
                {
                    await WaitForRemovalCompletionAsync(removedTcs.Task, TimeSpan.FromMilliseconds(2500));
                    DeleteDownloadedPaths(filePaths, topSubDir);
                }
            }
            finally
            {
                _pendingRemovals.TryRemove(manager.InfoHash, out _);
            }
        }

        if (!string.IsNullOrWhiteSpace(item.TorrentFilePath) && File.Exists(item.TorrentFilePath))
        {
            try { File.Delete(item.TorrentFilePath); } catch { }
        }

        string resumePath = GetResumePath(item.InfoHash);
        if (File.Exists(resumePath))
        {
            try { File.Delete(resumePath); } catch { }
        }
    }

    public Task SetFilePriorityAsync(TorrentItemModel item, TorrentFileModel file, TorrentFilePriority priority)
    {
        if (!_managers.TryGetValue(item.InfoHash, out var manager))
            return Task.CompletedTask;

        var target = manager.Files.FirstOrDefault(f =>
            f.Info.Index == file.FileIndex ||
            string.Equals(f.Info.Path, file.RelativePath, StringComparison.OrdinalIgnoreCase));

        if (target is not null)
        {
            target.Priority = ToLibtorrentPriority(priority);
            file.Priority = priority;
        }

        return Task.CompletedTask;
    }

    public Task SetAllFilesPriorityAsync(TorrentItemModel item, TorrentFilePriority priority)
    {
        if (!_managers.TryGetValue(item.InfoHash, out var manager))
            return Task.CompletedTask;

        var nativePriority = ToLibtorrentPriority(priority);
        foreach (var f in manager.Files)
            f.Priority = nativePriority;

        SyncFiles(manager, item);
        return Task.CompletedTask;
    }

    public Task AddTrackerAsync(TorrentItemModel item, string trackerUrl)
    {
        string? normalized = FormatHelper.TryNormalizeTrackerUri(trackerUrl);
        if (normalized is null)
            throw new FormatException(LocalizationService.Instance["ErrInvalidTrackerUrl"]);

        if (!_managers.TryGetValue(item.InfoHash, out var manager))
            return Task.CompletedTask;

        if (!AddTrackerUriIfMissing(manager, normalized))
            throw new InvalidOperationException(LocalizationService.Instance["ErrTrackerAlreadyAdded"]);

        if (!item.CustomTrackers.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            item.CustomTrackers.Add(normalized);

        if (!_pausedHashes.Contains(item.InfoHash))
        {
            manager.ReannounceAllTrackers(TimeSpan.Zero, true);
        }

        SyncTrackers(manager, item);
        SaveSessionState();
        return Task.CompletedTask;
    }

    public Task RemoveTrackerAsync(TorrentItemModel item, string trackerUri)
    {
        if (!_managers.TryGetValue(item.InfoHash, out var manager))
            return Task.CompletedTask;

        var remaining = manager.GetTrackers()
            .Where(t => !string.Equals(t.Url, trackerUri, StringComparison.OrdinalIgnoreCase))
            .Select(t => (t.Url, (byte)Math.Clamp(t.Tier, 0, byte.MaxValue)))
            .ToList();

        manager.ReplaceTrackers(remaining);
        item.CustomTrackers.RemoveAll(u => string.Equals(u, trackerUri, StringComparison.OrdinalIgnoreCase));
        SyncTrackers(manager, item);
        SaveSessionState();
        return Task.CompletedTask;
    }

    public Task ApplySettingsAsync(AppSettings settings)
    {
        _settingsService.SaveSettings(settings);
        if (_client is null)
            return Task.CompletedTask;

        _client.ChangeOptions(opts =>
        {
            opts.Notifications(ConfigureNotifications);
            opts.Network(n => ConfigureNetworkOptions(n, settings.ListenPort));

            opts.Discovery(d =>
            {
                d.Dht(settings.EnableDht, DefaultDhtRouters);
                d.Lsd(settings.EnableLocalPeerDiscovery);
                d.Upnp(settings.EnablePortForwarding);
                d.NatPmp(settings.EnablePortForwarding);
            });

            opts.Limits(l =>
            {
                l.Download(Math.Max(0, settings.MaxDownloadSpeedKBps) * 1024);
                l.Upload(Math.Max(0, settings.MaxUploadSpeedKBps) * 1024);
                l.Connections(Math.Clamp(settings.MaxConnections, 10, 2000));
                l.SeedingOutgoingConnections(true);
            });

            opts.Storage(s => s.DefaultDownloadPath(settings.DefaultDownloadPath));
            opts.Advanced(sp => ApplyAdvancedSettingsPack(sp, settings));
        });

        if (settings.DefaultTrackers.Count > 0)
        {
            foreach (var item in Torrents.ToList())
            {
                if (_managers.TryGetValue(item.InfoHash, out var manager))
                {
                    ApplyDefaultTrackersToManager(manager, settings.DefaultTrackers);
                    SyncTrackers(manager, item);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task RefreshDetailsAsync(TorrentItemModel item)
    {
        if (!_managers.TryGetValue(item.InfoHash, out var manager))
            return Task.CompletedTask;

        byte[] pieceMap = manager.PieceCount > 0 ? manager.GetPieceMap() : [];
        SyncFiles(manager, item, pieceMap);
        SyncTrackers(manager, item);
        SyncPieceMap(manager, item, pieceMap);
        SyncPeers(manager, item);
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync()
    {
        if (_disposed)
            return;

        LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
        _refreshTimer.Stop();
        _checkPumpTimer.Stop();
        lock (_checkQueueLock)
        {
            _pendingCheckQueue.Clear();
            _queuedCheckHashes.Clear();
            _keepPausedAfterCheck.Clear();
            _activeCheckingHash = null;
        }
        SaveSessionState();
        _settingsService.SaveTrafficStats(_trafficStats);

        if (_client is not null)
        {
            await SaveAllResumeDataAsync(forceAll: true).ConfigureAwait(false);

            try
            {
                byte[] sessionState = _client.GetState();
                if (sessionState.Length > 0)
                    await File.WriteAllBytesAsync(SessionStateFilePath, sessionState).ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                _client.Dispose();
            }
            catch
            {
            }
            _client = null;
        }

        IsRunning = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await ShutdownAsync();
        _disposed = true;
    }

    private void OnLanguageChanged()
    {
        if (_disposed || _client is null)
            return;

        foreach (var item in Torrents.ToList())
        {
            if (_managers.TryGetValue(item.InfoHash, out var manager))
            {
                SyncModel(manager, item);
                SyncTrackers(manager, item);
                SyncPeers(manager, item);
            }
            item.NotifyLocalizedTextsChanged();
        }
    }

    private void EnsureInitialized()
    {
        if (_client is null)
            throw new InvalidOperationException(LocalizationService.Instance["ErrEngineNotInitialized"]);
    }

    private void RegisterManager(TorrentManager manager, TorrentItemModel model)
    {
        _managers[model.InfoHash] = manager;
        RegisterTorrentNames(model.InfoHash, manager.Info?.Metadata.Name, model.Name);
        Torrents.Insert(0, model);
    }

    private async Task RefreshAllTickAsync()
    {
        if (_client is null || _disposed || _isRefreshingTick)
            return;

        _isRefreshingTick = true;
        try
        {
            AdvanceCheckQueue();

            long totalDown = 0;
            long totalUp = 0;
            long deltaDownBytes = 0;
            long deltaUpBytes = 0;

            foreach (var item in Torrents.ToList())
            {
                if (!_managers.TryGetValue(item.InfoHash, out var manager))
                    continue;

                var status = manager.GetCurrentStatus();
                SyncModel(manager, item, status);
                totalDown += item.DownloadSpeed;
                totalUp += item.UploadSpeed;

                if (item.StatusKind is TorrentStatusKind.Downloading or TorrentStatusKind.Metadata)
                {
                    if (PruneOfflineTrackers(manager, item) > 0)
                    {
                        SyncTrackers(manager, item);
                    }
                }

                long curDown = status.AllTimeDownload;
                long curUp = status.AllTimeUpload;
                if (_lastManagerBytes.TryGetValue(item.InfoHash, out var prev))
                {
                    if (curDown > prev.Down)
                        deltaDownBytes += curDown - prev.Down;
                    if (curUp > prev.Up)
                        deltaUpBytes += curUp - prev.Up;
                }
                _lastManagerBytes[item.InfoHash] = (curDown, curUp);
            }

            if (deltaDownBytes > 0 || deltaUpBytes > 0)
            {
                RecordTrafficDelta(_trafficStats, deltaDownBytes, deltaUpBytes, DateTime.Now);
                DailyTrafficHistory = BuildRecentDaysWindow(_trafficStats, DateTime.Now, 14);
                _trafficStatsDirty = true;
            }
            else if (DailyTrafficHistory.Count == 0)
            {
                DailyTrafficHistory = BuildRecentDaysWindow(_trafficStats, DateTime.Now, 14);
            }

            _trafficSaveTickCounter++;
            if (_trafficSaveTickCounter >= 120)
            {
                _trafficSaveTickCounter = 0;
                if (_trafficStatsDirty)
                {
                    _trafficStatsDirty = false;
                    _settingsService.SaveTrafficStats(_trafficStats);
                }
            }

            _resumeSaveTickCounter++;
            if (_resumeSaveTickCounter >= 120)
            {
                _resumeSaveTickCounter = 0;
                _ = SaveAllResumeDataAsync(forceAll: false);
            }

            TotalDownloadSpeed = totalDown;
            TotalUploadSpeed = totalUp;
            PurgeExpiredUploadingPieces(DateTime.UtcNow);

            _dhtPollTickCounter++;
            if (_dhtPollTickCounter >= 5)
            {
                _dhtPollTickCounter = 0;
                _ = PollDhtNodeCountAsync();
            }

            CheckAutoReannounce();

            StatsUpdated?.Invoke();
            await Task.CompletedTask;
        }
        finally
        {
            _isRefreshingTick = false;
        }
    }

    private async Task PollDhtNodeCountAsync()
    {
        if (_client is null || _disposed || _isRefreshingDhtNodes)
            return;

        if (!_settingsService.Current.EnableDht)
        {
            DhtNodesCount = 0;
            return;
        }

        _isRefreshingDhtNodes = true;
        try
        {
            var stats = await _client.GetSessionStatsAsync(CancellationToken.None, TimeSpan.FromMilliseconds(600));
            if (stats.TryGetValue("dht.dht_nodes", out long nodes))
                DhtNodesCount = (int)Math.Clamp(nodes, 0, int.MaxValue);
        }
        catch
        {
            // Ignore transient session stats timeouts
        }
        finally
        {
            _isRefreshingDhtNodes = false;
        }
    }

    private void CheckAutoReannounce()
    {
        int customInterval = _settingsService.Current.ReannounceIntervalSeconds;
        if (customInterval <= 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var item in Torrents.ToList())
        {
            if (_pausedHashes.Contains(item.InfoHash) ||
                item.StatusKind is not (TorrentStatusKind.Downloading or TorrentStatusKind.Seeding or TorrentStatusKind.Metadata))
            {
                continue;
            }

            if (!_managers.TryGetValue(item.InfoHash, out var manager))
                continue;

            if (!_lastForcedReannounceUtc.TryGetValue(item.InfoHash, out var last))
            {
                // Seed baseline on first sight so startup doesn't force-reannounce all torrents at t=0.
                _lastForcedReannounceUtc[item.InfoHash] = now;
                continue;
            }

            if ((now - last).TotalSeconds < customInterval)
            {
                continue;
            }

            _lastForcedReannounceUtc[item.InfoHash] = now;
            try
            {
                manager.ReannounceAllTrackers(TimeSpan.Zero, true);
            }
            catch
            {
            }

            // Stagger across 1s ticks instead of bursting hundreds of tracker sockets simultaneously.
            break;
        }
    }

    private void ApplyDefaultTrackersToManager(TorrentManager manager, IEnumerable<string> defaultTrackers)
    {
        string hash = NormalizeHash(manager.InfoHash);
        (_, bool isPrivate) = GetMetadataHints(manager, hash);
        if (isPrivate)
            return;

        foreach (string raw in defaultTrackers)
        {
            string? normalized = FormatHelper.TryNormalizeTrackerUri(raw);
            if (normalized is not null)
                AddTrackerUriIfMissing(manager, normalized);
        }
    }

    private static bool AddTrackerUriIfMissing(TorrentManager manager, string normalizedUri)
    {
        var trackers = manager.GetTrackers();
        if (trackers.Any(t => string.Equals(t.Url, normalizedUri, StringComparison.OrdinalIgnoreCase)))
            return false;

        int maxTier = trackers.Count > 0 ? trackers.Max(t => t.Tier) : -1;
        byte nextTier = (byte)Math.Clamp(maxTier + 1, 0, byte.MaxValue);
        manager.AddTracker(normalizedUri, nextTier);
        return true;
    }

    private static int PruneOfflineTrackers(TorrentManager manager, TorrentItemModel model)
    {
        var trackers = manager.GetTrackers();
        if (trackers.Count == 0)
            return 0;

        var offline = trackers.Where(IsTrackerOffline).ToList();
        if (offline.Count == 0)
            return 0;

        var remaining = trackers
            .Where(t => !IsTrackerOffline(t))
            .Select(t => (t.Url, (byte)Math.Clamp(t.Tier, 0, byte.MaxValue)))
            .ToList();

        manager.ReplaceTrackers(remaining);
        return offline.Count;
    }

    private static bool IsTrackerOffline(ConnectedTracker tracker) =>
        !tracker.Updating &&
        !tracker.Verified &&
        tracker.Fails > 0;

    private void SyncModel(TorrentManager manager, TorrentItemModel model, TorrentStatus? cachedStatus = null)
    {
        var st = cachedStatus ?? manager.GetCurrentStatus();
        string hash = model.InfoHash;

        bool hasMeta = manager.Info is not null && manager.Info.Metadata.TotalSize > 0;
        model.HasMetadata = hasMeta;
        if (hasMeta && !string.IsNullOrWhiteSpace(manager.Info!.Metadata.Name))
        {
            model.Name = manager.Info.Metadata.Name;
            RegisterTorrentName(hash, manager.Info.Metadata.Name);
        }

        model.TotalSize = manager.Info?.Metadata.TotalSize ?? 0;

        bool isQueuedForCheck = IsQueuedForCheck(hash);
        bool isPaused = !isQueuedForCheck && _pausedHashes.Contains(hash);
        bool isChecking = isQueuedForCheck || (!isPaused && IsCheckingState(st.State));
        bool isMetadata = !isPaused && !isQueuedForCheck && st.State == TorrentState.DownloadingMetadata;

        double progressPct;
        if (isMetadata)
        {
            progressPct = 0;
        }
        else if (isChecking)
        {
            progressPct = st.TotalWanted > 0
                ? Math.Clamp((double)st.TotalWantedDone / st.TotalWanted * 100.0, 0, 100)
                : 0;
        }
        else if (st.TotalWanted > 0)
        {
            progressPct = Math.Clamp((double)st.TotalWantedDone / st.TotalWanted * 100.0, 0, 100);
        }
        else
        {
            progressPct = Math.Clamp(st.Progress * 100.0, 0, 100);
        }

        model.Progress = progressPct;
        model.DownloadedBytes = st.TotalWantedDone > 0
            ? st.TotalWantedDone
            : (model.TotalSize > 0 ? (long)(model.TotalSize * (progressPct / 100.0)) : st.BytesDownloaded);
        model.UploadedBytes = st.AllTimeUpload > 0 ? st.AllTimeUpload : st.BytesUploaded;

        bool isSeedingState = st.State is TorrentState.Seeding or TorrentState.Finished;
        bool isInactive = isPaused || isChecking || st.State == TorrentState.Errored;

        model.DownloadSpeed = isInactive || isSeedingState ? 0 : Math.Max(0, st.DownloadRate);
        model.UploadSpeed = isInactive ? 0 : Math.Max(0, st.UploadRate);
        model.ConnectedPeers = isPaused ? 0 : st.ConnectionCount;
        model.TotalSeeders = st.SeedCount;
        model.TotalLeechers = Math.Max(0, st.PeerCount - st.SeedCount);
        model.SwarmTotalPeers = st.PeerCount;

        (int pieceLength, _) = GetMetadataHints(manager, hash);
        model.PieceLength = pieceLength;
        model.PieceCount = manager.PieceCount;

        if (manager.PieceCount > 0)
        {
            if (st.Progress >= 0.9999f || st.State is TorrentState.Seeding or TorrentState.Finished)
            {
                model.CompletedPieces = manager.PieceCount;
            }
            else if (!isPaused || model.CompletedPieces == 0)
            {
                byte[] map = manager.GetPieceMap();
                int doneCount = 0;
                for (int i = 0; i < map.Length; i++)
                {
                    if (map[i] != 0)
                        doneCount++;
                }
                model.CompletedPieces = doneCount;
            }
        }
        else
        {
            model.CompletedPieces = 0;
        }

        model.SavePath = _savePaths.TryGetValue(hash, out string? savedPath)
            ? savedPath
            : _settingsService.Current.DefaultDownloadPath;

        model.Ratio = model.DownloadedBytes > 0
            ? Math.Round((double)model.UploadedBytes / model.DownloadedBytes, 2)
            : 0;

        model.Eta = !isPaused && st.State == TorrentState.Downloading
            ? FormatHelper.CalculateEta(model.TotalSize, model.DownloadedBytes, model.DownloadSpeed)
            : null;

        var loc = LocalizationService.Instance;
        if (isQueuedForCheck)
        {
            model.StatusKind = TorrentStatusKind.Checking;
            model.StatusText = loc["StatusCheckingQueue"];
        }
        else if (isPaused)
        {
            bool completed = progressPct >= 99.99 ||
                             st.Progress >= 0.9999f ||
                             st.State is TorrentState.Seeding or TorrentState.Finished;
            model.StatusKind = completed ? TorrentStatusKind.Stopped : TorrentStatusKind.Paused;
            model.StatusText = completed ? loc["StatusCompleted"] : loc["StatusPaused"];
        }
        else
        {
            (model.StatusKind, model.StatusText) = st.State switch
            {
                TorrentState.Downloading => (TorrentStatusKind.Downloading, loc["StatusDownloading"]),
                TorrentState.Seeding or TorrentState.Finished => (TorrentStatusKind.Seeding, loc["StatusSeeding"]),
                TorrentState.CheckingFiles or TorrentState.CheckingResume => (TorrentStatusKind.Checking, loc["StatusChecking"]),
                TorrentState.DownloadingMetadata => (TorrentStatusKind.Metadata, loc["StatusMetadata"]),
                TorrentState.Errored => (TorrentStatusKind.Error, loc["StatusError"]),
                _ => (TorrentStatusKind.Stopped, loc["StatusStopped"])
            };
        }

        if (st.State != TorrentState.Errored)
            model.ErrorMessage = null;

        if (isInactive && model.UploadingPieces.Count > 0)
            UpdateModelUploadingPieces(model);
    }

    private void SyncFiles(TorrentManager manager, TorrentItemModel model, byte[]? sharedPieceMap = null)
    {
        var nativeFiles = manager.Files;
        if (nativeFiles.Count == 0)
            return;

        string savePath = _savePaths.TryGetValue(model.InfoHash, out string? sp)
            ? sp
            : _settingsService.Current.DefaultDownloadPath;

        if (model.Files.Count == nativeFiles.Count &&
            model.Progress >= 100.0 &&
            model.Files.All(f => f.BytesDownloaded >= f.Length))
        {
            for (int i = 0; i < nativeFiles.Count; i++)
            {
                model.Files[i].Priority = FromLibtorrentPriority(nativeFiles[i].Priority);
            }
            return;
        }

        (int pieceLength, _) = GetMetadataHints(manager, model.InfoHash);
        byte[] pieceMap = sharedPieceMap ?? (manager.PieceCount > 0 ? manager.GetPieceMap() : []);
        long totalSize = manager.Info?.Metadata.TotalSize ?? 0;

        if (model.Files.Count != nativeFiles.Count)
        {
            model.Files.Clear();
            foreach (var f in nativeFiles)
            {
                long doneBytes = ComputeFileDownloadedBytes(f.Info.Offset, f.Info.FileSize, pieceLength, totalSize, pieceMap);
                double pct = f.Info.FileSize > 0
                    ? Math.Clamp(doneBytes * 100.0 / f.Info.FileSize, 0, 100)
                    : 100;

                model.Files.Add(new TorrentFileModel
                {
                    FileIndex = f.Info.Index,
                    OffsetInTorrent = f.Info.Offset,
                    FullPath = Path.Combine(savePath, f.Info.Path),
                    RelativePath = f.Info.Path,
                    FileName = string.IsNullOrWhiteSpace(f.Info.Name) ? Path.GetFileName(f.Info.Path) : f.Info.Name,
                    Length = f.Info.FileSize,
                    BytesDownloaded = doneBytes,
                    Progress = pct,
                    Priority = FromLibtorrentPriority(f.Priority)
                });
            }
        }
        else
        {
            for (int i = 0; i < nativeFiles.Count; i++)
            {
                var src = nativeFiles[i];
                var dst = model.Files[i];
                long doneBytes = ComputeFileDownloadedBytes(src.Info.Offset, src.Info.FileSize, pieceLength, totalSize, pieceMap);
                dst.BytesDownloaded = doneBytes;
                dst.Progress = src.Info.FileSize > 0
                    ? Math.Clamp(doneBytes * 100.0 / src.Info.FileSize, 0, 100)
                    : 100;
                dst.Priority = FromLibtorrentPriority(src.Priority);
            }
        }
    }

    private static long ComputeFileDownloadedBytes(long fileOffset, long fileSize, int pieceLength, long totalTorrentSize, byte[] pieceMap)
    {
        if (fileSize <= 0 || pieceLength <= 0 || pieceMap.Length == 0)
            return 0;

        long fileEnd = fileOffset + fileSize;
        int firstPiece = Math.Clamp((int)(fileOffset / pieceLength), 0, pieceMap.Length - 1);
        int lastPiece = Math.Clamp((int)((fileEnd - 1) / pieceLength), firstPiece, pieceMap.Length - 1);

        long downloaded = 0;
        for (int p = firstPiece; p <= lastPiece; p++)
        {
            if (pieceMap[p] == 0)
                continue;

            long pieceStart = (long)p * pieceLength;
            long pieceEnd = totalTorrentSize > 0
                ? Math.Min(pieceStart + pieceLength, totalTorrentSize)
                : pieceStart + pieceLength;

            long overlapStart = Math.Max(fileOffset, pieceStart);
            long overlapEnd = Math.Min(fileEnd, pieceEnd);
            if (overlapEnd > overlapStart)
                downloaded += overlapEnd - overlapStart;
        }

        return Math.Clamp(downloaded, 0, fileSize);
    }

    private void SyncPieceMap(TorrentManager manager, TorrentItemModel model, byte[]? sharedPieceMap = null)
    {
        UpdateModelUploadingPieces(model);

        if (manager.PieceCount <= 0)
        {
            if (model.PieceBucketFill.Count > 0)
                model.PieceBucketFill = [];
            return;
        }

        byte[] map = sharedPieceMap ?? manager.GetPieceMap();
        if (map.Length == 0)
        {
            if (model.PieceBucketFill.Count > 0)
                model.PieceBucketFill = [];
            return;
        }

        const int maxSamples = 4096;
        int total = map.Length;
        if (total <= maxSamples)
        {
            var existing = model.PieceBucketFill;
            if (existing.Count == total)
            {
                bool changed = false;
                for (int i = 0; i < total; i++)
                {
                    double target = map[i] != 0 ? 1.0 : 0.0;
                    if (Math.Abs(existing[i] - target) > 0.0001)
                    {
                        changed = true;
                        break;
                    }
                }
                if (!changed)
                    return;
            }

            var cells = new double[total];
            for (int i = 0; i < total; i++)
                cells[i] = map[i] != 0 ? 1.0 : 0.0;
            model.PieceBucketFill = cells;
            return;
        }

        var existingBuckets = model.PieceBucketFill;
        double[]? buckets = existingBuckets.Count == maxSamples ? null : new double[maxSamples];
        bool bucketChanged = buckets is not null;

        for (int b = 0; b < maxSamples; b++)
        {
            int start = (int)((long)b * total / maxSamples);
            int end = (int)((long)(b + 1) * total / maxSamples);
            int count = Math.Max(1, end - start);
            int done = 0;
            for (int p = start; p < end; p++)
            {
                if (map[p] != 0)
                    done++;
            }
            double val = (double)done / count;
            if (buckets is null)
            {
                if (Math.Abs(existingBuckets[b] - val) > 0.0001)
                {
                    buckets = new double[maxSamples];
                    for (int k = 0; k < b; k++)
                        buckets[k] = existingBuckets[k];
                    buckets[b] = val;
                    bucketChanged = true;
                }
            }
            else
            {
                buckets[b] = val;
            }
        }

        if (bucketChanged && buckets is not null)
            model.PieceBucketFill = buckets;
    }

    private void UpdateModelUploadingPieces(TorrentItemModel model)
    {
        bool inactive = model.StatusKind is TorrentStatusKind.Paused or TorrentStatusKind.Stopped or TorrentStatusKind.Error;
        int[] snapshot = GetUploadingPiecesSnapshot(model.InfoHash, DateTime.UtcNow, clearIfInactive: inactive);
        if (model.UploadingPieces.Count == 0 && snapshot.Length == 0)
            return;

        if (model.UploadingPieces is int[] prev && prev.AsSpan().SequenceEqual(snapshot))
            return;

        model.UploadingPieces = snapshot;
    }

    /// <summary>
    /// Parses libtorrent's unmapped block_uploaded_alert message:
    /// "&lt;torrentName&gt; peer [ &lt;ip:port&gt; client: &lt;client&gt; ] block uploaded to a peer (piece: &lt;n&gt; block: &lt;b&gt;)".
    /// </summary>
    internal static bool TryParseBlockUploadedMessage(string? message, out string torrentName, out int pieceIndex)
    {
        const string uploadMarker = "] block uploaded to a peer (piece: ";
        const string peerPrefix = " peer [ ";
        const string blockMarker = " block: ";

        torrentName = string.Empty;
        pieceIndex = -1;

        if (string.IsNullOrWhiteSpace(message))
            return false;

        int markerIdx = message.IndexOf(uploadMarker, StringComparison.Ordinal);
        if (markerIdx <= 0)
            return false;

        int peerIdx = message.LastIndexOf(peerPrefix, markerIdx, StringComparison.Ordinal);
        if (peerIdx <= 0)
            return false;

        int pieceStart = markerIdx + uploadMarker.Length;
        int blockIdx = message.IndexOf(blockMarker, pieceStart, StringComparison.Ordinal);
        if (blockIdx <= pieceStart)
            return false;

        if (!int.TryParse(message.AsSpan(pieceStart, blockIdx - pieceStart), out pieceIndex) || pieceIndex < 0)
            return false;

        torrentName = message[..peerIdx];
        return torrentName.Length > 0;
    }

    internal void RegisterTorrentNames(string infoHash, params string?[] names)
    {
        string normalizedHash = NormalizeHash(infoHash);
        if (string.IsNullOrWhiteSpace(normalizedHash))
            return;

        lock (_uploadTrackLock)
        {
            RegisterNameLocked(normalizedHash, normalizedHash);
            foreach (string? name in names)
                RegisterNameLocked(normalizedHash, name);
        }
    }

    private void RegisterTorrentName(string infoHash, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        string normalizedHash = NormalizeHash(infoHash);
        lock (_uploadTrackLock)
        {
            RegisterNameLocked(normalizedHash, name);
        }
    }

    private void RegisterNameLocked(string normalizedHash, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        if (!_registeredNamesByHash.TryGetValue(normalizedHash, out var registered))
        {
            registered = new HashSet<string>(StringComparer.Ordinal);
            _registeredNamesByHash[normalizedHash] = registered;
        }

        if (!registered.Add(name))
            return;

        if (!_torrentNameToHashes.TryGetValue(name, out var hashes))
        {
            hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _torrentNameToHashes[name] = hashes;
        }

        hashes.Add(normalizedHash);
    }

    internal void UnregisterTorrentUploadTracking(string infoHash)
    {
        string normalizedHash = NormalizeHash(infoHash);
        lock (_uploadTrackLock)
        {
            _recentUploadingPieces.Remove(normalizedHash);
            if (_registeredNamesByHash.Remove(normalizedHash, out var names))
            {
                foreach (string name in names)
                {
                    if (_torrentNameToHashes.TryGetValue(name, out var hashes))
                    {
                        hashes.Remove(normalizedHash);
                        if (hashes.Count == 0)
                            _torrentNameToHashes.Remove(name);
                    }
                }
            }
        }
    }

    internal void RecordUploadedPiece(string torrentName, int pieceIndex, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(torrentName) || pieceIndex < 0)
            return;

        lock (_uploadTrackLock)
        {
            if (!_torrentNameToHashes.TryGetValue(torrentName, out var hashes) || hashes.Count == 0)
                return;

            foreach (string hash in hashes)
            {
                if (!_recentUploadingPieces.TryGetValue(hash, out var pieceTimes))
                {
                    pieceTimes = new Dictionary<int, DateTime>();
                    _recentUploadingPieces[hash] = pieceTimes;
                }

                pieceTimes[pieceIndex] = utcNow;
            }
        }
    }

    internal int[] GetUploadingPiecesSnapshot(string infoHash, DateTime utcNow, bool clearIfInactive)
    {
        string normalizedHash = NormalizeHash(infoHash);
        lock (_uploadTrackLock)
        {
            if (clearIfInactive)
            {
                _recentUploadingPieces.Remove(normalizedHash);
                return [];
            }

            if (!_recentUploadingPieces.TryGetValue(normalizedHash, out var pieceTimes) || pieceTimes.Count == 0)
                return [];

            PruneExpiredLocked(pieceTimes, utcNow);
            if (pieceTimes.Count == 0)
            {
                _recentUploadingPieces.Remove(normalizedHash);
                return [];
            }

            return pieceTimes.Keys.ToArray();
        }
    }

    private void PurgeExpiredUploadingPieces(DateTime utcNow)
    {
        lock (_uploadTrackLock)
        {
            if (_recentUploadingPieces.Count == 0)
                return;

            List<string>? emptyHashes = null;
            foreach (var (hash, pieceTimes) in _recentUploadingPieces)
            {
                PruneExpiredLocked(pieceTimes, utcNow);
                if (pieceTimes.Count == 0)
                    (emptyHashes ??= []).Add(hash);
            }

            if (emptyHashes is not null)
            {
                foreach (string hash in emptyHashes)
                    _recentUploadingPieces.Remove(hash);
            }
        }
    }

    private static void PruneExpiredLocked(Dictionary<int, DateTime> pieceTimes, DateTime utcNow)
    {
        List<int>? expired = null;
        foreach (var (piece, lastSeenUtc) in pieceTimes)
        {
            if (utcNow - lastSeenUtc > UploadingPieceVisibilityWindow)
                (expired ??= []).Add(piece);
        }

        if (expired is not null)
        {
            foreach (int piece in expired)
                pieceTimes.Remove(piece);
        }
    }

    private void SyncTrackers(TorrentManager manager, TorrentItemModel model)
    {
        var updated = new List<TorrentTrackerModel>();
        int customInterval = _settingsService.Current.ReannounceIntervalSeconds;

        var loc = LocalizationService.Instance;
        foreach (var tracker in manager.GetTrackers())
        {
            (string label, bool healthy, bool error, string statusName) = tracker.Updating
                ? (loc["TrackerConnecting"], false, false, "Connecting")
                : tracker.Verified
                    ? (loc["TrackerOk"], true, false, "Ok")
                    : tracker.Fails > 0
                        ? (loc["TrackerOffline"], false, true, "Offline")
                        : (loc["TrackerUnknown"], false, false, "Unknown");

            var parts = new List<string>();
            parts.Add(tracker.Verified ? loc["TrackerAnnounced"] : tracker.Updating ? loc["TrackerAnnouncing"] : loc["TrackerWaitingAnnounce"]);
            if (customInterval > 0)
                parts.Add(loc.Format("TrackerTimeoutForcedFormat", customInterval));
            if (!tracker.Verified)
            {
                if (tracker.Fails > 0)
                    parts.Add(loc.Format("TrackerFailsFormat", tracker.Fails));
                if (!string.IsNullOrWhiteSpace(tracker.FailureMessage))
                    parts.Add(tracker.FailureMessage);
                else if (!string.IsNullOrWhiteSpace(tracker.WarningMessage))
                    parts.Add(tracker.WarningMessage);
            }
            else if (!string.IsNullOrWhiteSpace(tracker.WarningMessage))
            {
                parts.Add(tracker.WarningMessage);
            }

            updated.Add(new TorrentTrackerModel
            {
                Uri = tracker.Url,
                Status = statusName,
                StatusLabel = label,
                IsHealthy = healthy,
                IsError = error,
                TierNumber = tracker.Tier + 1,
                DetailsLine = string.Join(" • ", parts),
                FailureMessage = tracker.Verified ? string.Empty : (tracker.FailureMessage ?? string.Empty)
            });
        }

        // Update in place when count matches so delete buttons inside rows stay stable during ticks
        if (model.Trackers.Count == updated.Count)
        {
            for (int i = 0; i < updated.Count; i++)
            {
                var cur = model.Trackers[i];
                var nxt = updated[i];
                if (!string.Equals(cur.Uri, nxt.Uri, StringComparison.Ordinal) ||
                    !string.Equals(cur.StatusLabel, nxt.StatusLabel, StringComparison.Ordinal) ||
                    !string.Equals(cur.DetailsLine, nxt.DetailsLine, StringComparison.Ordinal) ||
                    cur.TierNumber != nxt.TierNumber)
                {
                    model.Trackers[i] = nxt;
                }
            }
        }
        else
        {
            model.Trackers.Clear();
            foreach (var item in updated)
                model.Trackers.Add(item);
        }
    }

    private static void SyncPeers(TorrentManager manager, TorrentItemModel model)
    {
        try
        {
            bool inactive = model.StatusKind is TorrentStatusKind.Paused or TorrentStatusKind.Stopped or TorrentStatusKind.Error;
            if (inactive)
            {
                model.Peers.Clear();
                return;
            }

            var peers = manager.GetPeers();
            Dictionary<string, double>? peerProgressByEndpoint = peers.Count > 0
                ? TryGetPeerProgressMap(manager)
                : null;
            var peerModels = new List<TorrentPeerModel>(peers.Count);
            foreach (var p in peers)
            {
                string endpoint = FormatHelper.FormatPeerEndpoint(p.Address);
                double progressPercent = p.IsSeed
                    ? 100.0
                    : (peerProgressByEndpoint is not null && peerProgressByEndpoint.TryGetValue(endpoint, out double nativeProgress)
                        ? nativeProgress
                        : 0.0);

                peerModels.Add(new TorrentPeerModel
                {
                    Endpoint = endpoint,
                    ClientName = string.IsNullOrWhiteSpace(p.Client) ? LocalizationService.Instance["PeerClientUnknown"] : p.Client,
                    IsSeeder = p.IsSeed,
                    ProgressPercent = progressPercent,
                    DownloadSpeed = p.DownloadRate,
                    UploadSpeed = p.UploadRate,
                    EncryptionType = p.EncryptionType.ToString(),
                    Direction = p.Direction.ToString()
                });
            }

            model.Peers.Clear();
            foreach (var sorted in SortPeers(peerModels).Take(100))
                model.Peers.Add(sorted);
        }
        catch
        {
        }
    }

    private TorrentItemModel CreateModelFromManager(
        TorrentManager manager,
        string savePath,
        string? torrentFilePath,
        string? magnetUri,
        DateTime addedAt)
    {
        string infoHash = NormalizeHash(manager.InfoHash);
        string name = !string.IsNullOrWhiteSpace(manager.Info?.Metadata.Name)
            ? manager.Info.Metadata.Name
            : (ExtractDisplayNameFromMagnet(magnetUri) ?? infoHash);

        var model = new TorrentItemModel
        {
            InfoHash = infoHash,
            Name = name,
            SavePath = savePath,
            TorrentFilePath = torrentFilePath,
            MagnetUri = magnetUri ?? BuildMagnetUri(manager),
            AddedAt = addedAt
        };

        RefreshMetadataHints(manager, infoHash);
        SyncFiles(manager, model);
        SyncTrackers(manager, model);
        return model;
    }

    private (int PieceLength, bool IsPrivate) GetMetadataHints(TorrentManager manager, string hash)
    {
        if (_metadataHints.TryGetValue(hash, out var cached) && cached.PieceLength > 0)
            return cached;

        return RefreshMetadataHints(manager, hash);
    }

    private (int PieceLength, bool IsPrivate) RefreshMetadataHints(TorrentManager manager, string hash)
    {
        try
        {
            if (manager.Info is not null)
            {
                byte[] bytes = manager.Info.GetBytes();
                var hints = BencodeHelper.ReadMetadataHints(bytes);
                if (hints.PieceLength > 0 || hints.IsPrivate)
                {
                    _metadataHints[hash] = hints;
                    return hints;
                }
            }
        }
        catch
        {
        }

        return (0, false);
    }

    private void RecalculateTotalSpeeds()
    {
        TotalDownloadSpeed = Torrents.Sum(t => t.DownloadSpeed);
        TotalUploadSpeed = Torrents.Sum(t => t.UploadSpeed);
    }

    private void SaveSessionState()
    {
        var entries = Torrents.Select(t => new SavedTorrentEntry
        {
            InfoHash = t.InfoHash,
            Name = t.Name,
            SavePath = t.SavePath,
            TorrentFilePath = t.TorrentFilePath,
            MagnetUri = t.MagnetUri,
            WasPaused = t.StatusKind is TorrentStatusKind.Paused or TorrentStatusKind.Stopped,
            AddedAt = t.AddedAt,
            CustomTrackers = t.CustomTrackers.ToList()
        });
        _settingsService.SaveSession(entries);
    }

    private async Task SaveAllResumeDataAsync(bool forceAll)
    {
        if (_isSavingResumeData || _client is null)
            return;

        _isSavingResumeData = true;
        try
        {
            var snapshot = _managers.ToList();
            foreach (var (hash, manager) in snapshot)
            {
                if (_client is null)
                    break;

                try
                {
                    var st = manager.GetCurrentStatus();
                    if (!forceAll)
                    {
                        if (IsCheckingState(st.State) || st.State == TorrentState.DownloadingMetadata)
                            continue;

                        var signature = (st.TotalWantedDone, st.AllTimeUpload, st.State);
                        if (_lastSavedResumeState.TryGetValue(hash, out var lastSig) && lastSig == signature)
                            continue;
                    }

                    byte[] resumeData = await manager.SaveResumeDataAsync(CancellationToken.None, TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                    if (resumeData is { Length: > 0 })
                    {
                        string path = GetResumePath(hash);
                        await Task.Run(() => File.WriteAllBytes(path, resumeData)).ConfigureAwait(false);
                        _lastSavedResumeState[hash] = (st.TotalWantedDone, st.AllTimeUpload, st.State);
                    }
                }
                catch
                {
                }
            }
        }
        finally
        {
            _isSavingResumeData = false;
        }
    }

    private string GetResumePath(string infoHash) =>
        Path.Combine(FastResumeDirectory, $"{NormalizeHash(infoHash)}.resume");

    private byte[]? LoadResumeBytes(string infoHash)
    {
        string path = GetResumePath(infoHash);
        if (!File.Exists(path))
            return null;

        try { return File.ReadAllBytes(path); } catch { return null; }
    }

    private static string BuildMagnetUri(TorrentManager manager)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("magnet:?xt=urn:btih:");
        sb.Append(NormalizeHash(manager.InfoHash));

        string? name = manager.Info?.Metadata.Name;
        if (!string.IsNullOrWhiteSpace(name))
        {
            sb.Append("&dn=");
            sb.Append(Uri.EscapeDataString(name));
        }

        foreach (string tr in manager.GetTrackers().Select(t => t.Url).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("&tr=");
            sb.Append(Uri.EscapeDataString(tr));
        }

        return sb.ToString();
    }

    private static string? ExtractInfoHashFromMagnet(string magnetUri)
    {
        const string marker = "urn:btih:";
        int idx = magnetUri.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        int start = idx + marker.Length;
        int end = magnetUri.IndexOf('&', start);
        string raw = end >= 0 ? magnetUri[start..end] : magnetUri[start..];
        return string.IsNullOrWhiteSpace(raw) ? null : NormalizeHash(raw);
    }

    private static string? ExtractDisplayNameFromMagnet(string? magnetUri)
    {
        if (string.IsNullOrWhiteSpace(magnetUri))
            return null;

        const string marker = "dn=";
        int idx = magnetUri.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        int start = idx + marker.Length;
        int end = magnetUri.IndexOf('&', start);
        string raw = end >= 0 ? magnetUri[start..end] : magnetUri[start..];
        try { return Uri.UnescapeDataString(raw.Replace('+', ' ')); } catch { return raw; }
    }

    private static string? ExtractTopSubDirectory(string savePath, string firstRelativePath)
    {
        string normalized = firstRelativePath.Replace('/', '\\').TrimStart('\\');
        int sep = normalized.IndexOf('\\');
        if (sep <= 0)
            return null;

        string dir = Path.Combine(savePath, normalized[..sep]);
        return Directory.Exists(dir) ? dir : null;
    }

    private static async Task WaitForRemovalCompletionAsync(Task removalTask, TimeSpan timeout)
    {
        try
        {
            await removalTask.WaitAsync(timeout);
        }
        catch
        {
        }
    }

    private static void DeleteDownloadedPaths(IEnumerable<string> filePaths, string? torrentSubFolder)
    {
        foreach (string path in filePaths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        if (!string.IsNullOrWhiteSpace(torrentSubFolder) && Directory.Exists(torrentSubFolder))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(torrentSubFolder).Any())
                    Directory.Delete(torrentSubFolder, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static string NormalizeHash(string? hash) => (hash ?? string.Empty).Trim().ToUpperInvariant();

    private static FileDownloadPriority ToLibtorrentPriority(TorrentFilePriority priority) => priority switch
    {
        TorrentFilePriority.DoNotDownload => FileDownloadPriority.DoNotDownload,
        TorrentFilePriority.Low => FileDownloadPriority.Low,
        TorrentFilePriority.High => FileDownloadPriority.High,
        _ => FileDownloadPriority.Normal
    };

    private static TorrentFilePriority FromLibtorrentPriority(FileDownloadPriority priority) => priority switch
    {
        FileDownloadPriority.DoNotDownload => TorrentFilePriority.DoNotDownload,
        FileDownloadPriority.Low => TorrentFilePriority.Low,
        FileDownloadPriority.High => TorrentFilePriority.High,
        _ => TorrentFilePriority.Normal
    };

    public static IEnumerable<TorrentPeerModel> SortPeers(IEnumerable<TorrentPeerModel> peers) =>
        peers
            .OrderByDescending(p => p.IsSeeder)
            .ThenByDescending(p => p.DownloadSpeed + p.UploadSpeed)
            .ThenByDescending(p => p.ProgressPercent);

    public static void RecordTrafficDelta(TrafficStatsData stats, long downDelta, long upDelta, DateTime localNow)
    {
        if (downDelta <= 0 && upDelta <= 0)
            return;

        stats.TotalDownloadedBytes += Math.Max(0, downDelta);
        stats.TotalUploadedBytes += Math.Max(0, upDelta);

        string key = localNow.ToString("yyyy-MM-dd");
        var day = stats.Days.FirstOrDefault(d => string.Equals(d.DateKey, key, StringComparison.Ordinal));
        if (day is null)
        {
            day = new DailyTrafficPoint { DateKey = key };
            stats.Days.Add(day);
        }

        day.DownloadedBytes += Math.Max(0, downDelta);
        day.UploadedBytes += Math.Max(0, upDelta);

        if (stats.Days.Count > 90)
        {
            stats.Days = stats.Days
                .OrderByDescending(d => d.DateKey, StringComparer.Ordinal)
                .Take(90)
                .OrderBy(d => d.DateKey, StringComparer.Ordinal)
                .ToList();
        }
    }

    public static IReadOnlyList<DailyTrafficPoint> BuildRecentDaysWindow(TrafficStatsData stats, DateTime localNow, int dayCount = 14)
    {
        var map = stats.Days.ToDictionary(d => d.DateKey, d => d, StringComparer.Ordinal);
        var result = new List<DailyTrafficPoint>(dayCount);
        var startDate = localNow.Date.AddDays(-(dayCount - 1));

        for (int i = 0; i < dayCount; i++)
        {
            var date = startDate.AddDays(i);
            string key = date.ToString("yyyy-MM-dd");
            if (map.TryGetValue(key, out var existing))
            {
                result.Add(new DailyTrafficPoint
                {
                    DateKey = key,
                    DownloadedBytes = existing.DownloadedBytes,
                    UploadedBytes = existing.UploadedBytes
                });
            }
            else
            {
                result.Add(new DailyTrafficPoint
                {
                    DateKey = key,
                    DownloadedBytes = 0,
                    UploadedBytes = 0
                });
            }
        }

        return result;
    }

    internal static bool IsCheckingState(TorrentState state) =>
        state is TorrentState.CheckingFiles or TorrentState.CheckingResume;

    internal bool IsQueuedForCheck(string infoHash)
    {
        string normalized = NormalizeHash(infoHash);
        lock (_checkQueueLock)
            return _queuedCheckHashes.Contains(normalized);
    }

    internal string? CurrentCheckingInfoHash
    {
        get
        {
            lock (_checkQueueLock)
                return _activeCheckingHash;
        }
    }

    internal int QueuedCheckCount
    {
        get
        {
            lock (_checkQueueLock)
                return _queuedCheckHashes.Count;
        }
    }

    private bool IsCheckBusyForOther(string hash)
    {
        lock (_checkQueueLock)
            return _activeCheckingHash is not null && !string.Equals(_activeCheckingHash, hash, StringComparison.OrdinalIgnoreCase);
    }

    private void CancelPendingCheck(string rawHash)
    {
        string hash = NormalizeHash(rawHash);
        lock (_checkQueueLock)
        {
            _queuedCheckHashes.Remove(hash);
            _keepPausedAfterCheck.Remove(hash);
            if (string.Equals(_activeCheckingHash, hash, StringComparison.OrdinalIgnoreCase))
                _activeCheckingHash = null;
        }
    }

    private void StartOrQueueCheck(string rawHash, TorrentManager manager, TorrentItemModel? model, bool callForceRecheck, bool keepPausedAfterCheck)
    {
        string hash = NormalizeHash(rawHash);
        bool mustQueue;

        lock (_checkQueueLock)
        {
            if (_activeCheckingHash is not null &&
                !string.Equals(_activeCheckingHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                if (_managers.TryGetValue(_activeCheckingHash, out var activeMgr) &&
                    !IsCheckingState(activeMgr.GetCurrentStatus().State))
                {
                    FinalizeCompletedActiveCheckLocked(_activeCheckingHash, activeMgr);
                    _activeCheckingHash = null;
                }
            }

            mustQueue = _activeCheckingHash is not null &&
                        !string.Equals(_activeCheckingHash, hash, StringComparison.OrdinalIgnoreCase);

            if (mustQueue)
            {
                if (!_queuedCheckHashes.Contains(hash))
                {
                    _pendingCheckQueue.Enqueue(new PendingCheckEntry(hash, callForceRecheck));
                    _queuedCheckHashes.Add(hash);
                }

                _keepPausedAfterCheck[hash] = keepPausedAfterCheck;
                _pausedHashes.Remove(hash);
            }
            else
            {
                _queuedCheckHashes.Remove(hash);
                _activeCheckingHash = hash;
                if (keepPausedAfterCheck)
                    _keepPausedAfterCheck[hash] = true;
                else
                    _keepPausedAfterCheck.Remove(hash);
                _pausedHashes.Remove(hash);
            }
        }

        if (mustQueue)
        {
            manager.Stop();
            EnsureCheckPumpRunning();
            if (model is not null)
                SyncModel(manager, model);
            return;
        }

        if (callForceRecheck)
            manager.ForceRecheck();

        manager.Start();

        var state = manager.GetCurrentStatus().State;
        if (!IsCheckingState(state))
        {
            AdvanceCheckQueue();
        }
        else
        {
            EnsureCheckPumpRunning();
        }

        if (model is not null)
            SyncModel(manager, model);
    }

    private void FinalizeCompletedActiveCheckLocked(string finishedHash, TorrentManager? finishedManager)
    {
        if (_keepPausedAfterCheck.Remove(finishedHash, out bool keepPaused) && keepPaused)
        {
            _pausedHashes.Add(finishedHash);
            finishedManager?.Stop();
        }
    }

    private void ScheduleCheckQueueAdvance()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            if (!dispatcher.HasShutdownStarted)
                dispatcher.BeginInvoke(AdvanceCheckQueue);
        }
        else
        {
            _ = Task.Run(AdvanceCheckQueue);
        }
    }

    internal void AdvanceCheckQueue()
    {
        if (_client is null || _disposed)
            return;

        try
        {
            while (true)
            {
                TorrentManager? nextManager = null;
                PendingCheckEntry? nextEntry = null;
                TorrentManager? completedManager = null;
                string? completedHash = null;

                lock (_checkQueueLock)
                {
                    if (_client is null || _disposed)
                        return;

                    if (_activeCheckingHash is not null)
                    {
                        if (_managers.TryGetValue(_activeCheckingHash, out var activeMgr))
                        {
                            if (IsCheckingState(activeMgr.GetCurrentStatus().State))
                            {
                                EnsureCheckPumpRunning();
                                return;
                            }

                            completedHash = _activeCheckingHash;
                            completedManager = activeMgr;
                            FinalizeCompletedActiveCheckLocked(completedHash, completedManager);
                        }

                        _activeCheckingHash = null;
                    }

                    while (_pendingCheckQueue.Count > 0)
                    {
                        var candidate = _pendingCheckQueue.Dequeue();
                        if (!_queuedCheckHashes.Remove(candidate.InfoHash))
                            continue;

                        if (_managers.TryGetValue(candidate.InfoHash, out nextManager))
                        {
                            nextEntry = candidate;
                            _activeCheckingHash = candidate.InfoHash;
                            _pausedHashes.Remove(candidate.InfoHash);
                            break;
                        }
                    }

                    if (nextEntry is null)
                    {
                        StopCheckPumpIfIdleLocked();
                    }
                }

                if (completedHash is not null && completedManager is not null)
                {
                    var completedModel = Torrents.FirstOrDefault(t => string.Equals(t.InfoHash, completedHash, StringComparison.OrdinalIgnoreCase));
                    if (completedModel is not null)
                        SyncModel(completedManager, completedModel);
                }

                if (nextEntry is null || nextManager is null)
                    return;

                if (nextEntry.CallForceRecheck)
                    nextManager.ForceRecheck();

                nextManager.Start();

                var nextModel = Torrents.FirstOrDefault(t => string.Equals(t.InfoHash, nextEntry.InfoHash, StringComparison.OrdinalIgnoreCase));
                var nextStatus = nextManager.GetCurrentStatus();
                if (nextModel is not null)
                    SyncModel(nextManager, nextModel, nextStatus);

                if (IsCheckingState(nextStatus.State))
                {
                    EnsureCheckPumpRunning();
                    return;
                }
            }
        }
        catch
        {
        }
    }

    private void EnsureCheckPumpRunning()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

        if (dispatcher.CheckAccess())
        {
            if (!_checkPumpTimer.IsEnabled)
                _checkPumpTimer.Start();
        }
        else
        {
            dispatcher.BeginInvoke(() =>
            {
                if (!_disposed && !_checkPumpTimer.IsEnabled)
                    _checkPumpTimer.Start();
            });
        }
    }

    private void StopCheckPumpIfIdleLocked()
    {
        if (_activeCheckingHash is not null || _pendingCheckQueue.Count > 0)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

        if (dispatcher.CheckAccess())
            _checkPumpTimer.Stop();
        else
            dispatcher.BeginInvoke(() => _checkPumpTimer.Stop());
    }

    private sealed record PendingCheckEntry(string InfoHash, bool CallForceRecheck);

    internal static double ConvertPeerProgressPpmToPercent(int progressPpm, bool isSeed)
    {
        if (isSeed)
            return 100.0;

        return Math.Clamp(progressPpm / 10_000.0, 0.0, 100.0);
    }

    private static Dictionary<string, double>? TryGetPeerProgressMap(TorrentManager manager)
    {
        if (TorrentManagerHandleField is null)
            return null;

        if (TorrentManagerHandleField.GetValue(manager) is not nint handle || handle == IntPtr.Zero)
            return null;

        NativePeerProgressList list = default;
        try
        {
            GetTorrentPeerProgress(handle, out list);
            if (list.Length <= 0 || list.Peers == IntPtr.Zero)
                return null;

            var result = new Dictionary<string, double>(list.Length, StringComparer.OrdinalIgnoreCase);
            int stride = Marshal.SizeOf<NativePeerProgressInfo>();
            for (int i = 0; i < list.Length; i++)
            {
                nint itemPtr = list.Peers + (i * stride);
                NativePeerProgressInfo item = Marshal.PtrToStructure<NativePeerProgressInfo>(itemPtr);
                if (item.Address == IntPtr.Zero)
                    continue;

                string rawAddress = Marshal.PtrToStringUTF8(item.Address) ?? string.Empty;
                string endpoint = FormatHelper.FormatPeerEndpoint(rawAddress);
                if (!string.IsNullOrEmpty(endpoint))
                    result[endpoint] = ConvertPeerProgressPpmToPercent(item.ProgressPpm, isSeed: false);
            }

            return result;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (list.Peers != IntPtr.Zero)
            {
                try
                {
                    DestroyTorrentPeerProgressList(ref list);
                }
                catch
                {
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePeerProgressInfo
    {
        public nint Address;
        public int ProgressPpm;
        public int Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePeerProgressList
    {
        public int Length;
        public nint Peers;
    }

    [DllImport("tsw", CallingConvention = CallingConvention.Cdecl, EntryPoint = "get_torrent_peer_progress")]
    private static extern void GetTorrentPeerProgress(nint torrentHandle, out NativePeerProgressList list);

    [DllImport("tsw", CallingConvention = CallingConvention.Cdecl, EntryPoint = "destroy_torrent_peer_progress_list")]
    private static extern void DestroyTorrentPeerProgressList(ref NativePeerProgressList list);
}
