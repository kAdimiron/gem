using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using Gem.Services;
using Xunit;

namespace Gem.Tests;

public sealed class GitHubUpdateServiceTests : IDisposable
{
    private readonly string _tempDir;

    public GitHubUpdateServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GemUpdateTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
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

    [Theory]
    [InlineData("user/Gem", "user/Gem")]
    [InlineData("https://github.com/user/Gem", "user/Gem")]
    [InlineData("https://github.com/user/Gem.git", "user/Gem")]
    [InlineData("https://github.com/user/Gem/releases", "user/Gem")]
    [InlineData("https://api.github.com/repos/user/Gem/releases/latest", "user/Gem")]
    [InlineData("", null)]
    [InlineData("invalid_single_segment", null)]
    [InlineData("https://example.com/user/Gem", null)]
    public void TryNormalizeRepository_ParsesExpectedFormats(string input, string? expected)
    {
        Assert.Equal(expected, GitHubUpdateService.TryNormalizeRepository(input));
    }

    [Fact]
    public void TryParseVersion_NormalizesVersionsAndTags()
    {
        Assert.Equal(new Version(1, 2, 3, 0), GitHubUpdateService.TryParseVersion("v1.2.3"));
        Assert.Equal(new Version(2, 5, 0, 0), GitHubUpdateService.TryParseVersion("V2.5-beta.1"));
        Assert.Null(GitHubUpdateService.TryParseVersion("not-a-version"));
    }

    [Fact]
    public void SelectBestAsset_PrefersWindowsZipOverExeAndSkipsChecksums()
    {
        var assets = new[]
        {
            new UpdateAssetInfo("Gem-1.1.0-win-x64.zip.sha256", "https://example.com/sha", 64),
            new UpdateAssetInfo("Gem-Setup-1.1.0.exe", "https://example.com/exe", 10_000_000),
            new UpdateAssetInfo("Gem-1.1.0-win-x64.zip", "https://example.com/zip", 8_000_000)
        };

        var selected = GitHubUpdateService.SelectBestAsset(assets);
        Assert.NotNull(selected);
        Assert.Equal("Gem-1.1.0-win-x64.zip", selected.Name);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_DetectsNewerReleaseFromGitHubJson()
    {
        const string json = """
        {
          "tag_name": "v99.1.0",
          "name": "Gem 99.1.0",
          "body": "Bug fixes and speed improvements",
          "html_url": "https://github.com/test/Gem/releases/tag/v99.1.0",
          "draft": false,
          "assets": [
            {
              "name": "Gem-99.1.0-win-x64.zip",
              "browser_download_url": "https://github.com/test/Gem/releases/download/v99.1.0/Gem-99.1.0-win-x64.zip",
              "size": 1024
            }
          ]
        }
        """;

        using var http = new HttpClient(new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            }));

        var settingsService = new SettingsService(_tempDir);
        var service = new GitHubUpdateService(settingsService, http);

        var result = await service.CheckForUpdatesAsync("https://github.com/test/Gem");

        Assert.Null(result.ErrorMessage);
        Assert.True(result.IsUpdateAvailable);
        Assert.NotNull(result.LatestRelease);
        Assert.Equal("v99.1.0", result.LatestRelease.TagName);
        Assert.Equal("Gem-99.1.0-win-x64.zip", result.LatestRelease.Asset?.Name);
    }

    [Fact]
    public async Task DownloadAndPrepareUpdateAsync_ExtractsZipAndCreatesApplyScript()
    {
        string payloadSrcDir = Path.Combine(_tempDir, "src_payload", "Gem");
        Directory.CreateDirectory(payloadSrcDir);
        await File.WriteAllTextAsync(Path.Combine(payloadSrcDir, "Gem.exe"), "fake-exe");
        string zipPath = Path.Combine(_tempDir, "release.zip");
        ZipFile.CreateFromDirectory(Path.Combine(_tempDir, "src_payload"), zipPath);
        byte[] zipBytes = await File.ReadAllBytesAsync(zipPath);

        using var http = new HttpClient(new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(zipBytes)
            }));

        var settingsService = new SettingsService(Path.Combine(_tempDir, "appdata"));
        var service = new GitHubUpdateService(settingsService, http);
        var release = new UpdateReleaseInfo(
            new Version(2, 0, 0, 0),
            "v2.0.0",
            "Gem 2.0.0",
            "Notes",
            "https://github.com/test/Gem/releases/tag/v2.0.0",
            new UpdateAssetInfo("Gem-2.0.0-win-x64.zip", "https://example.com/release.zip", zipBytes.Length));

        string scriptPath = await service.DownloadAndPrepareUpdateAsync(release);

        try
        {
            Assert.True(File.Exists(scriptPath));
            string scriptContent = await File.ReadAllTextAsync(scriptPath);
            Assert.Contains("xcopy", scriptContent);
            Assert.Contains(Path.Combine("extracted", "Gem"), scriptContent);
            Assert.Contains("start \"\"", scriptContent);
        }
        finally
        {
            if (File.Exists(scriptPath))
                File.Delete(scriptPath);
        }
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
