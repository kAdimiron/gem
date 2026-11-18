using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace Gem.Services;

public sealed partial class GitHubUpdateService : IUpdateService
{
    private readonly SettingsService _settingsService;
    private readonly HttpClient _httpClient;

    public GitHubUpdateService(SettingsService settingsService, HttpClient? httpClient = null)
    {
        _settingsService = settingsService;
        _httpClient = httpClient ?? CreateDefaultHttpClient(GetEntryVersion());
    }

    public Version CurrentVersion { get; } = GetEntryVersion();

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(string repository, CancellationToken cancellationToken = default)
    {
        string? normalizedRepo = TryNormalizeRepository(repository);
        if (normalizedRepo is null)
        {
            return new UpdateCheckResult(
                CurrentVersion,
                null,
                false,
                LocalizationService.Instance["ErrGitHubInvalidRepo"]);
        }

        try
        {
            string url = $"https://api.github.com/repos/{normalizedRepo}/releases/latest";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd($"Gem/{CurrentVersion.ToString(3)}");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    CurrentVersion,
                    null,
                    false,
                    LocalizationService.Instance.Format("ErrGitHubHttpError", (int)response.StatusCode, response.ReasonPhrase));
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            var release = ParseReleaseJson(json);
            if (release is null)
            {
                return new UpdateCheckResult(
                    CurrentVersion,
                    null,
                    false,
                    LocalizationService.Instance["ErrGitHubVersionMissing"]);
            }

            bool isNewer = NormalizeComparableVersion(release.Version) > NormalizeComparableVersion(CurrentVersion);
            return new UpdateCheckResult(CurrentVersion, release, isNewer);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(CurrentVersion, null, false, ex.Message);
        }
    }

    public async Task<string> DownloadAndPrepareUpdateAsync(
        UpdateReleaseInfo release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (release.Asset is null || string.IsNullOrWhiteSpace(release.Asset.DownloadUrl))
            throw new InvalidOperationException(LocalizationService.Instance["ErrGitHubAssetNotFound"]);

        string safeTag = SanitizeFileName(release.TagName);
        string updatesRoot = Path.Combine(
            Path.GetDirectoryName(_settingsService.CacheDirectory) ?? Path.GetTempPath(),
            "updates",
            safeTag);

        if (Directory.Exists(updatesRoot))
            Directory.Delete(updatesRoot, recursive: true);
        Directory.CreateDirectory(updatesRoot);

        string safeAssetName = SanitizeFileName(release.Asset.Name);
        string downloadPath = Path.Combine(updatesRoot, safeAssetName);

        using (var request = new HttpRequestMessage(HttpMethod.Get, release.Asset.DownloadUrl))
        {
            request.Headers.UserAgent.ParseAdd($"Gem/{CurrentVersion.ToString(3)}");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? release.Asset.SizeBytes;
            await using var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = File.Create(downloadPath);

            byte[] buffer = new byte[64 * 1024];
            long received = 0;
            int bytesRead;
            while ((bytesRead = await remoteStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                received += bytesRead;
                if (totalBytes > 0)
                {
                    progress?.Report(Math.Clamp(received * 100.0 / totalBytes, 0, 100));
                }
            }
            progress?.Report(100);
        }

        string ext = Path.GetExtension(safeAssetName).ToLowerInvariant();
        string targetDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? processPath = Environment.ProcessPath;
        string gemExe = Path.Combine(targetDir, "Gem.exe");
        string currentExe = !string.IsNullOrWhiteSpace(processPath) &&
                            !Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? processPath
            : gemExe;

        if (ext == ".zip")
        {
            string extractDir = Path.Combine(updatesRoot, "extracted");
            ZipFile.ExtractToDirectory(downloadPath, extractDir, overwriteFiles: true);
            string payloadDir = ResolveExtractedPayloadRoot(extractDir, Path.GetFileName(currentExe));
            string scriptPath = Path.Combine(Path.GetTempPath(), $"Gem_apply_{safeTag}.cmd");
            string script = BuildApplyUpdateScript(
                Environment.ProcessId,
                payloadDir,
                targetDir,
                currentExe,
                updatesRoot,
                isSingleFileExe: false);
            await File.WriteAllTextAsync(scriptPath, script, Encoding.UTF8, cancellationToken);
            return scriptPath;
        }

        if (ext == ".exe" &&
            !safeAssetName.Contains("setup", StringComparison.OrdinalIgnoreCase) &&
            !safeAssetName.Contains("install", StringComparison.OrdinalIgnoreCase))
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), $"Gem_apply_{safeTag}.cmd");
            string script = BuildApplyUpdateScript(
                Environment.ProcessId,
                downloadPath,
                targetDir,
                currentExe,
                updatesRoot,
                isSingleFileExe: true);
            await File.WriteAllTextAsync(scriptPath, script, Encoding.UTF8, cancellationToken);
            return scriptPath;
        }

        return downloadPath;
    }

    public void LaunchPreparedUpdateAndExit(string preparedLauncherPath)
    {
        if (string.IsNullOrWhiteSpace(preparedLauncherPath) || !File.Exists(preparedLauncherPath))
            throw new FileNotFoundException(LocalizationService.Instance["ErrUpdateLauncherNotFound"], preparedLauncherPath);

        string ext = Path.GetExtension(preparedLauncherPath).ToLowerInvariant();
        var psi = ext is ".cmd" or ".bat"
            ? new ProcessStartInfo("cmd.exe", $"/c \"{preparedLauncherPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }
            : new ProcessStartInfo(preparedLauncherPath)
            {
                UseShellExecute = true
            };

        Process.Start(psi);
        Application.Current?.Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
    }

    public static UpdateReleaseInfo? ParseReleaseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("draft", out var draftProp) && draftProp.GetBoolean())
            return null;

        string tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? string.Empty : string.Empty;
        string name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? tagName : tagName;
        string body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? string.Empty : string.Empty;
        string htmlUrl = root.TryGetProperty("html_url", out var htmlProp) ? htmlProp.GetString() ?? string.Empty : string.Empty;

        Version? parsed = TryParseVersion(tagName) ?? TryParseVersion(name);
        if (parsed is null)
            return null;

        var assets = new List<UpdateAssetInfo>();
        if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsProp.EnumerateArray())
            {
                string assetName = asset.TryGetProperty("name", out var an) ? an.GetString() ?? string.Empty : string.Empty;
                string downloadUrl = asset.TryGetProperty("browser_download_url", out var du) ? du.GetString() ?? string.Empty : string.Empty;
                long size = asset.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long bytes) ? bytes : 0;

                if (!string.IsNullOrWhiteSpace(assetName) && !string.IsNullOrWhiteSpace(downloadUrl))
                    assets.Add(new UpdateAssetInfo(assetName, downloadUrl, size));
            }
        }

        return new UpdateReleaseInfo(
            parsed,
            string.IsNullOrWhiteSpace(tagName) ? $"v{parsed.ToString(3)}" : tagName,
            string.IsNullOrWhiteSpace(name) ? tagName : name,
            body.Trim(),
            htmlUrl,
            SelectBestAsset(assets));
    }

    public static UpdateAssetInfo? SelectBestAsset(IEnumerable<UpdateAssetInfo> assets)
    {
        var candidates = assets
            .Where(a =>
            {
                string lower = a.Name.ToLowerInvariant();
                return !lower.EndsWith(".sha256", StringComparison.Ordinal) &&
                       !lower.EndsWith(".sha512", StringComparison.Ordinal) &&
                       !lower.EndsWith(".sig", StringComparison.Ordinal) &&
                       !lower.EndsWith(".asc", StringComparison.Ordinal) &&
                       !lower.EndsWith(".pdb", StringComparison.Ordinal);
            })
            .ToList();

        if (candidates.Count == 0)
            return null;

        return candidates
            .OrderByDescending(a => ScoreAsset(a.Name))
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    public static string? TryNormalizeRepository(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string trimmed = raw.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            string[] segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) &&
                segments.Length >= 3 &&
                segments[0].Equals("repos", StringComparison.OrdinalIgnoreCase))
            {
                return ValidateOwnerRepo(segments[1], segments[2]);
            }

            if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                segments.Length >= 2)
            {
                string repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                    ? segments[1][..^4]
                    : segments[1];
                return ValidateOwnerRepo(segments[0], repo);
            }

            return null;
        }

        string[] parts = trimmed.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? ValidateOwnerRepo(parts[0], parts[1]) : null;
    }

    public static Version? TryParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var match = VersionRegex().Match(raw.Trim());
        if (!match.Success)
            return null;

        return Version.TryParse(match.Groups[1].Value, out var parsed)
            ? NormalizeComparableVersion(parsed)
            : null;
    }

    public static Version NormalizeComparableVersion(Version version) =>
        new(
            Math.Max(0, version.Major),
            Math.Max(0, version.Minor),
            Math.Max(0, version.Build),
            Math.Max(0, version.Revision));

    public static string BuildApplyUpdateScript(
        int processId,
        string sourcePath,
        string targetDir,
        string exeToLaunch,
        string stagingRoot,
        bool isSingleFileExe)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine(":wait_loop");
        sb.AppendLine($"tasklist /FI \"PID eq {processId}\" 2>nul | find \"{processId}\" >nul");
        sb.AppendLine("if not errorlevel 1 (");
        sb.AppendLine("    timeout /t 1 /nobreak >nul");
        sb.AppendLine("    goto wait_loop");
        sb.AppendLine(")");

        if (isSingleFileExe)
        {
            sb.AppendLine($"copy /Y \"{sourcePath}\" \"{exeToLaunch}\" >nul");
        }
        else
        {
            sb.AppendLine($"xcopy \"{sourcePath}\\*\" \"{targetDir}\\\" /E /I /Y /Q >nul");
        }

        sb.AppendLine($"start \"\" \"{exeToLaunch}\"");
        sb.AppendLine($"rd /s /q \"{stagingRoot}\" 2>nul");
        sb.AppendLine("del \"%~f0\" 2>nul");
        return sb.ToString();
    }

    private static int ScoreAsset(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower.EndsWith(".zip", StringComparison.Ordinal))
        {
            if (lower.Contains("win", StringComparison.Ordinal) ||
                lower.Contains("x64", StringComparison.Ordinal) ||
                lower.Contains("Gem", StringComparison.Ordinal))
            {
                return 100;
            }
            return 80;
        }

        if (lower.EndsWith(".exe", StringComparison.Ordinal))
            return 60;

        if (lower.EndsWith(".msi", StringComparison.Ordinal) || lower.EndsWith(".msix", StringComparison.Ordinal))
            return 50;

        return 10;
    }

    private static string ResolveExtractedPayloadRoot(string extractDir, string exeName)
    {
        if (File.Exists(Path.Combine(extractDir, exeName)))
            return extractDir;

        string[] subDirs = Directory.GetDirectories(extractDir);
        string[] files = Directory.GetFiles(extractDir);
        if (files.Length == 0 && subDirs.Length == 1)
            return subDirs[0];

        return extractDir;
    }

    private static string? ValidateOwnerRepo(string owner, string repo)
    {
        if (!RepoSegmentRegex().IsMatch(owner) || !RepoSegmentRegex().IsMatch(repo))
            return null;

        return $"{owner}/{repo}";
    }

    private static string SanitizeFileName(string name)
    {
        string file = Path.GetFileName(name);
        foreach (char invalid in Path.GetInvalidFileNameChars())
            file = file.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(file) ? "update_asset" : file;
    }

    private static Version GetEntryVersion()
    {
        var v = Assembly.GetEntryAssembly()?.GetName().Version ?? typeof(GitHubUpdateService).Assembly.GetName().Version;
        return v is not null ? NormalizeComparableVersion(v) : new Version(1, 0, 0, 0);
    }

    private static HttpClient CreateDefaultHttpClient(Version version)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Gem/{version.ToString(3)}");
        return client;
    }

    [GeneratedRegex(@"^[Vv]?(\d+\.\d+(?:\.\d+){0,2})(?:[-+].*)?$")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+$")]
    private static partial Regex RepoSegmentRegex();
}
