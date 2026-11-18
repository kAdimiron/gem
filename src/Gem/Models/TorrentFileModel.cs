using CommunityToolkit.Mvvm.ComponentModel;

namespace Gem.Models;

public sealed partial class TorrentFileModel : ObservableObject
{
    public int FileIndex { get; init; }

    public long OffsetInTorrent { get; init; }

    public string FullPath { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public string RelativePath { get; init; } = string.Empty;

    public long Length { get; init; }

    [ObservableProperty]
    private long _bytesDownloaded;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelected))]
    private TorrentFilePriority _priority = TorrentFilePriority.Normal;

    public bool IsSelected => Priority != TorrentFilePriority.DoNotDownload;
}
