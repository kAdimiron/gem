namespace Gem.Services;

public sealed record UpdateAssetInfo(string Name, string DownloadUrl, long SizeBytes);

public sealed record UpdateReleaseInfo(
    Version Version,
    string TagName,
    string Title,
    string ReleaseNotes,
    string HtmlUrl,
    UpdateAssetInfo? Asset);

public sealed record UpdateCheckResult(
    Version CurrentVersion,
    UpdateReleaseInfo? LatestRelease,
    bool IsUpdateAvailable,
    string? ErrorMessage = null);

public interface IUpdateService
{
    Version CurrentVersion { get; }

    Task<UpdateCheckResult> CheckForUpdatesAsync(string repository, CancellationToken cancellationToken = default);

    Task<string> DownloadAndPrepareUpdateAsync(
        UpdateReleaseInfo release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    void LaunchPreparedUpdateAndExit(string preparedLauncherPath);
}
