using Gem.Services;

namespace Gem.Models;

public sealed class TorrentPeerModel
{
    public string Endpoint { get; init; } = string.Empty;

    public string ClientName { get; init; } = string.Empty;

    public bool IsSeeder { get; init; }

    public string RoleBadge => IsSeeder
        ? LocalizationService.Instance["PeerRoleSeed"]
        : LocalizationService.Instance["PeerRolePeer"];

    public double ProgressPercent { get; init; }

    public long DownloadSpeed { get; init; }

    public long UploadSpeed { get; init; }

    public string EncryptionType { get; init; } = string.Empty;

    public string Direction { get; init; } = string.Empty;

    public string DirectionRu => Direction.Contains("Incoming", StringComparison.OrdinalIgnoreCase)
        ? LocalizationService.Instance["PeerDirectionIncoming"]
        : LocalizationService.Instance["PeerDirectionOutgoing"];
}
