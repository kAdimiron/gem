using System.Collections.ObjectModel;
using Gem.Models;

namespace Gem.Services;

public interface ITorrentEngineService : IAsyncDisposable
{
    ObservableCollection<TorrentItemModel> Torrents { get; }

    long TotalDownloadSpeed { get; }

    long TotalUploadSpeed { get; }

    long AllTimeDownloadedBytes { get; }

    long AllTimeUploadedBytes { get; }

    IReadOnlyList<DailyTrafficPoint> DailyTrafficHistory { get; }

    int DhtNodesCount { get; }

    bool IsRunning { get; }

    event Action? StatsUpdated;

    Task InitializeAsync();

    Task<TorrentItemModel> AddTorrentFileAsync(string torrentFilePath, string? savePath = null, bool? startImmediately = null);

    Task<TorrentItemModel> AddMagnetAsync(string magnetUri, string? savePath = null, bool? startImmediately = null);

    Task StartAsync(TorrentItemModel item);

    Task PauseAsync(TorrentItemModel item);

    Task StopAsync(TorrentItemModel item);

    Task ForceRecheckAsync(TorrentItemModel item);

    Task ReannounceAsync(TorrentItemModel item);

    Task RemoveAsync(TorrentItemModel item, bool deleteFiles);

    Task SetFilePriorityAsync(TorrentItemModel item, TorrentFileModel file, TorrentFilePriority priority);

    Task SetAllFilesPriorityAsync(TorrentItemModel item, TorrentFilePriority priority);

    Task AddTrackerAsync(TorrentItemModel item, string trackerUrl);

    Task RemoveTrackerAsync(TorrentItemModel item, string trackerUri);

    Task ApplySettingsAsync(AppSettings settings);

    Task RefreshDetailsAsync(TorrentItemModel item);

    Task ShutdownAsync();
}
