using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Gem.Helpers;

namespace Gem.Models;

public sealed partial class TorrentItemModel : ObservableObject
{
    public string InfoHash { get; init; } = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _savePath = string.Empty;

    [ObservableProperty]
    private string? _magnetUri;

    [ObservableProperty]
    private string? _torrentFilePath;

    public DateTime AddedAt { get; init; } = DateTime.UtcNow;

    [ObservableProperty]
    private long _totalSize;

    [ObservableProperty]
    private long _downloadedBytes;

    [ObservableProperty]
    private long _uploadedBytes;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private long _downloadSpeed;

    [ObservableProperty]
    private long _uploadSpeed;

    [ObservableProperty]
    private TimeSpan? _eta;

    [ObservableProperty]
    private double _ratio;

    [ObservableProperty]
    private int _connectedPeers;

    [ObservableProperty]
    private int _totalSeeders;

    [ObservableProperty]
    private int _totalLeechers;

    [ObservableProperty]
    private int _pieceCount;

    [ObservableProperty]
    private int _completedPieces;

    [ObservableProperty]
    private int _pieceLength;

    [ObservableProperty]
    private int _hashFails;

    [ObservableProperty]
    private int _swarmTotalPeers;

    [ObservableProperty]
    private IReadOnlyList<double> _pieceBucketFill = [];

    [ObservableProperty]
    private IReadOnlyCollection<int> _uploadingPieces = [];

    [ObservableProperty]
    private TorrentStatusKind _statusKind = TorrentStatusKind.Stopped;

    [ObservableProperty]
    private string _statusText = Services.LocalizationService.Instance["StatusStopped"];

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _hasMetadata = true;

    public bool IsActiveTransfer =>
        StatusKind is TorrentStatusKind.Downloading or TorrentStatusKind.Seeding or TorrentStatusKind.Metadata or TorrentStatusKind.Checking;

    public bool CanStart =>
        StatusKind is TorrentStatusKind.Paused or TorrentStatusKind.Stopped or TorrentStatusKind.Error;

    public bool CanPause =>
        StatusKind is TorrentStatusKind.Downloading or TorrentStatusKind.Metadata or TorrentStatusKind.Checking;

    public bool CanStop =>
        StatusKind == TorrentStatusKind.Seeding;

    public bool IsCompleted => Progress >= 99.99 || StatusKind == TorrentStatusKind.Seeding;

    public string PrimarySpeedDisplay =>
        StatusKind == TorrentStatusKind.Seeding || IsCompleted
            ? $"↑ {FormatHelper.FormatSpeed(UploadSpeed)}"
            : $"↓ {FormatHelper.FormatSpeed(DownloadSpeed)}";

    public ObservableCollection<TorrentFileModel> Files { get; } = new();

    public ObservableCollection<TorrentPeerModel> Peers { get; } = new();

    public ObservableCollection<TorrentTrackerModel> Trackers { get; } = new();

    public List<string> CustomTrackers { get; } = [];

    partial void OnStatusKindChanged(TorrentStatusKind value)
    {
        OnPropertyChanged(nameof(IsActiveTransfer));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(PrimarySpeedDisplay));
    }

    partial void OnProgressChanged(double value)
    {
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(PrimarySpeedDisplay));
    }

    partial void OnDownloadSpeedChanged(long value)
    {
        OnPropertyChanged(nameof(PrimarySpeedDisplay));
    }

    partial void OnUploadSpeedChanged(long value)
    {
        OnPropertyChanged(nameof(PrimarySpeedDisplay));
    }

    public void NotifyLocalizedTextsChanged()
    {
        OnPropertyChanged(nameof(DownloadedBytes));
        OnPropertyChanged(nameof(TotalSize));
        OnPropertyChanged(nameof(DownloadSpeed));
        OnPropertyChanged(nameof(UploadSpeed));
        OnPropertyChanged(nameof(Eta));
        OnPropertyChanged(nameof(PieceLength));
        OnPropertyChanged(nameof(UploadedBytes));
        OnPropertyChanged(nameof(PrimarySpeedDisplay));
        OnPropertyChanged(nameof(StatusText));
    }
}
