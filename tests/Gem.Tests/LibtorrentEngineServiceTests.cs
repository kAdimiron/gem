using System.IO;
using Gem.Helpers;
using Gem.Models;
using Gem.Services;
using Xunit;

namespace Gem.Tests;

[Collection("Localization")]
public sealed class LibtorrentEngineServiceTests : IDisposable
{
    private readonly string _tempDir;

    public LibtorrentEngineServiceTests()
    {
        LocalizationService.Instance.SetLanguage("ru-RU");
        _tempDir = Path.Combine(Path.GetTempPath(), "Gem_LibtorrentTests_" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void BencodeHelper_ExtractsPieceLengthAndPrivateFlag()
    {
        byte[] bytes = BencodeHelper.BuildMinimalTorrent("demo.bin", 524288, 2 * 1024 * 1024, "udp://tracker.opentrackr.org:1337/announce");
        (int pieceLength, bool isPrivate) = BencodeHelper.ReadMetadataHints(bytes);

        Assert.Equal(524288, pieceLength);
        Assert.False(isPrivate);
    }

    [Fact]
    public async Task LibtorrentEngine_SupportsAddTrackersFilePriorityAndFastRemoval()
    {
        string appData = Path.Combine(_tempDir, "appdata");
        string downloadDir = Path.Combine(_tempDir, "downloads");
        Directory.CreateDirectory(downloadDir);

        var settingsService = new SettingsService(appData);
        settingsService.SaveSettings(new AppSettings
        {
            DefaultDownloadPath = downloadDir,
            ListenPort = 0,
            EnableDht = false,
            EnableLocalPeerDiscovery = false,
            EnablePortForwarding = false,
            DefaultTrackers = ["udp://open.stealth.si:80/announce"]
        });

        string torrentPath = Path.Combine(_tempDir, "sample.torrent");
        File.WriteAllBytes(
            torrentPath,
            BencodeHelper.BuildMinimalTorrent("payload.bin", 262144, 262144L * 4, "http://10.255.255.1:6969/announce"));

        await using var engine = new LibtorrentEngineService(settingsService);
        await engine.InitializeAsync();
        Assert.True(engine.IsRunning);

        TorrentItemModel item = await engine.AddTorrentFileAsync(torrentPath, downloadDir, startImmediately: true);
        Assert.Single(engine.Torrents);
        Assert.Equal(262144, item.PieceLength);
        Assert.Equal(4, item.PieceCount);
        Assert.Contains(item.Trackers, t => t.Uri.Contains("open.stealth.si", StringComparison.OrdinalIgnoreCase));

        await engine.AddTrackerAsync(item, "https://tracker.example.org/announce");
        Assert.Contains(item.Trackers, t => t.Uri == "https://tracker.example.org/announce");

        await engine.RemoveTrackerAsync(item, "https://tracker.example.org/announce");
        Assert.DoesNotContain(item.Trackers, t => t.Uri == "https://tracker.example.org/announce");

        Assert.Single(item.Files);
        await engine.SetFilePriorityAsync(item, item.Files[0], TorrentFilePriority.DoNotDownload);
        Assert.Equal(TorrentFilePriority.DoNotDownload, item.Files[0].Priority);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await engine.RemoveAsync(item, deleteFiles: true);
        sw.Stop();

        Assert.Empty(engine.Torrents);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Expected fast removal, took {sw.Elapsed.TotalMilliseconds:F0} ms.");
    }

    [Fact]
    public async Task MaxConnections_AppliesPerTorrentIndependentlyAndUpdatesOnApplySettings()
    {
        string appData = Path.Combine(_tempDir, "appdata_conn");
        string downloadDir = Path.Combine(_tempDir, "downloads_conn");
        Directory.CreateDirectory(downloadDir);

        var settingsService = new SettingsService(appData);
        settingsService.SaveSettings(new AppSettings
        {
            DefaultDownloadPath = downloadDir,
            ListenPort = 0,
            EnableDht = false,
            EnableLocalPeerDiscovery = false,
            EnablePortForwarding = false,
            MaxConnections = 100
        });

        string t1Path = Path.Combine(_tempDir, "first.torrent");
        string t2Path = Path.Combine(_tempDir, "second.torrent");
        File.WriteAllBytes(t1Path, BencodeHelper.BuildMinimalTorrent("first.bin", 262144, 262144L * 2, "http://10.255.255.1:6969/announce"));
        File.WriteAllBytes(t2Path, BencodeHelper.BuildMinimalTorrent("second.bin", 262144, 262144L * 2, "http://10.255.255.1:6969/announce"));

        await using var engine = new LibtorrentEngineService(settingsService);
        await engine.InitializeAsync();

        var item1 = await engine.AddTorrentFileAsync(t1Path, downloadDir, startImmediately: false);
        var item2 = await engine.AddTorrentFileAsync(t2Path, downloadDir, startImmediately: false);

        Assert.Equal(100, engine.GetTorrentMaxConnections(item1.InfoHash));
        Assert.Equal(100, engine.GetTorrentMaxConnections(item2.InfoHash));

        var updated = settingsService.LoadSettings();
        updated.MaxConnections = 65;
        await engine.ApplySettingsAsync(updated);

        Assert.Equal(65, engine.GetTorrentMaxConnections(item1.InfoHash));
        Assert.Equal(65, engine.GetTorrentMaxConnections(item2.InfoHash));
    }

    [Fact]
    public void BuildRoutableListenInterfaces_ExcludesLoopbackAndLinkLocal()
    {
        var ifaces = LibtorrentEngineService.BuildRoutableListenInterfaces(55123);
        foreach (var iface in ifaces)
        {
            var ipIface = Assert.IsType<TorrentSharp.Wrap.Utils.IpInterface>(iface);
            Assert.False(System.Net.IPAddress.IsLoopback(ipIface.Endpoint.Address));
            byte[] bytes = ipIface.Endpoint.Address.GetAddressBytes();
            Assert.False(bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254);
        }
    }

    [Theory]
    [InlineData("test.bin peer [ 127.0.0.1:49978 client: LE 14.12.0.2 ] block uploaded to a peer (piece: 42 block: 3)", true, "test.bin", 42)]
    [InlineData("VisionQuest [2160p] peer [ 192.168.1.5:51413 client: qBittorrent 5.0.0 ] block uploaded to a peer (piece: 1205 block: 0)", true, "VisionQuest [2160p]", 1205)]
    [InlineData("test.bin peer [ 127.0.0.1:49978 client: LE 14.12.0.2 ]: incoming request [ piece: 1 start: 0 length: 16384 ]", false, "", -1)]
    [InlineData("", false, "", -1)]
    public void TryParseBlockUploadedMessage_ParsesLibtorrentAlertText(
        string message,
        bool expectedOk,
        string expectedTorrent,
        int expectedPiece)
    {
        bool ok = LibtorrentEngineService.TryParseBlockUploadedMessage(message, out string torrentName, out int pieceIndex);
        Assert.Equal(expectedOk, ok);
        Assert.Equal(expectedTorrent, torrentName);
        Assert.Equal(expectedPiece, pieceIndex);
    }

    [Fact]
    public void UploadingPiecesTracking_RecordsExpiresAndClearsCorrectly()
    {
        var settingsService = new SettingsService(Path.Combine(_tempDir, "appdata2"));
        var engine = new LibtorrentEngineService(settingsService);
        string hash = "0123456789ABCDEF0123456789ABCDEF01234567";
        DateTime t0 = new(2026, 11, 17, 6, 0, 0, DateTimeKind.Utc);

        engine.RegisterTorrentNames(hash, "VisionQuest.S01.2160p");
        engine.RecordUploadedPiece("VisionQuest.S01.2160p", 10, t0);
        engine.RecordUploadedPiece("VisionQuest.S01.2160p", 25, t0.AddSeconds(1.0));

        int[] activeAt1_5s = engine.GetUploadingPiecesSnapshot(hash, t0.AddSeconds(1.5), clearIfInactive: false);
        Assert.Equal([10, 25], activeAt1_5s.OrderBy(x => x).ToArray());

        int[] activeAt3s = engine.GetUploadingPiecesSnapshot(hash, t0.AddSeconds(3.0), clearIfInactive: false);
        Assert.Equal([25], activeAt3s);

        int[] paused = engine.GetUploadingPiecesSnapshot(hash, t0.AddSeconds(3.1), clearIfInactive: true);
        Assert.Empty(paused);

        engine.RecordUploadedPiece("VisionQuest.S01.2160p", 99, t0.AddSeconds(4.0));
        engine.UnregisterTorrentUploadTracking(hash);
        Assert.Empty(engine.GetUploadingPiecesSnapshot(hash, t0.AddSeconds(4.1), clearIfInactive: false));
    }

    [Fact]
    public async Task BencodeHelper_CreateTorrentAsync_BuildsValidSingleAndMultiFileTorrents()
    {
        // 1. Single-file torrent
        string singleFile = Path.Combine(_tempDir, "single.bin");
        byte[] payload1 = new byte[40_000];
        Random.Shared.NextBytes(payload1);
        await File.WriteAllBytesAsync(singleFile, payload1);

        var singleResult = await BencodeHelper.CreateTorrentAsync(new TorrentCreationOptions(
            singleFile,
            ["udp://tracker.opentrackr.org:1337/announce", "https://tracker.example.org/announce"],
            PieceLengthBytes: 16 * 1024,
            IsPrivate: true,
            Comment: "Single release"));

        Assert.Equal("single.bin", singleResult.TorrentName);
        Assert.Equal(_tempDir, singleResult.SeedSavePath);
        Assert.Equal(40_000, singleResult.TotalSize);
        Assert.Equal(3, singleResult.PieceCount);
        Assert.Equal(1, singleResult.FileCount);

        var singleInfo = new TorrentSharp.Wrap.TorrentInfo(singleResult.TorrentBytes);
        Assert.Equal("single.bin", singleInfo.Metadata.Name);
        Assert.Equal(40_000, singleInfo.Metadata.TotalSize);
        Assert.Single(singleInfo.Files);

        (int pl, bool priv) = BencodeHelper.ReadMetadataHints(singleResult.TorrentBytes);
        Assert.Equal(16 * 1024, pl);
        Assert.True(priv);

        // 2. Multi-file folder torrent
        string releaseDir = Path.Combine(_tempDir, "MyRelease");
        string subDir = Path.Combine(releaseDir, "Sub");
        Directory.CreateDirectory(subDir);
        await File.WriteAllBytesAsync(Path.Combine(releaseDir, "alpha.dat"), payload1[..20_000]);
        await File.WriteAllBytesAsync(Path.Combine(subDir, "beta.dat"), payload1[20_000..]);

        var multiResult = await BencodeHelper.CreateTorrentAsync(new TorrentCreationOptions(
            releaseDir,
            [],
            PieceLengthBytes: 16 * 1024,
            IsPrivate: false));

        Assert.Equal("MyRelease", multiResult.TorrentName);
        Assert.Equal(_tempDir, multiResult.SeedSavePath);
        Assert.Equal(40_000, multiResult.TotalSize);
        Assert.Equal(2, multiResult.FileCount);

        var multiInfo = new TorrentSharp.Wrap.TorrentInfo(multiResult.TorrentBytes);
        Assert.Equal("MyRelease", multiInfo.Metadata.Name);
        Assert.Equal(40_000, multiInfo.Metadata.TotalSize);
        Assert.Equal(2, multiInfo.Files.Count);
    }

    [Fact]
    public async Task LibtorrentEngine_ChecksTorrentsSequentiallyInsteadOfAllAtOnce()
    {
        string appData = Path.Combine(_tempDir, "appdata_seq");
        string dataDir = Path.Combine(_tempDir, "seq_data");
        Directory.CreateDirectory(dataDir);

        var settingsService = new SettingsService(appData);
        settingsService.SaveSettings(new AppSettings
        {
            DefaultDownloadPath = dataDir,
            ListenPort = 0,
            EnableDht = false,
            EnableLocalPeerDiscovery = false,
            EnablePortForwarding = false
        });

        string[] names = ["check1.bin", "check2.bin", "check3.bin"];
        var torrentPaths = new List<string>();
        byte[] payload = new byte[8 * 1024 * 1024];
        Random.Shared.NextBytes(payload);

        foreach (string name in names)
        {
            string filePath = Path.Combine(dataDir, name);
            await File.WriteAllBytesAsync(filePath, payload);
            var created = await BencodeHelper.CreateTorrentAsync(new TorrentCreationOptions(
                filePath,
                [],
                PieceLengthBytes: 256 * 1024));
            string tPath = Path.Combine(_tempDir, $"{name}.torrent");
            await File.WriteAllBytesAsync(tPath, created.TorrentBytes);
            torrentPaths.Add(tPath);
        }

        await using var engine = new LibtorrentEngineService(settingsService);
        await engine.InitializeAsync();

        var items = new List<TorrentItemModel>();
        foreach (string tPath in torrentPaths)
        {
            items.Add(await engine.AddTorrentFileAsync(tPath, dataDir, startImmediately: false));
        }

        // Trigger recheck on all 3 at once; only the first should check immediately while the rest queue.
        await engine.ForceRecheckAsync(items[0]);
        await engine.ForceRecheckAsync(items[1]);
        await engine.ForceRecheckAsync(items[2]);

        Assert.Equal(items[0].InfoHash, engine.CurrentCheckingInfoHash, ignoreCase: true);
        Assert.True(engine.IsQueuedForCheck(items[1].InfoHash));
        Assert.True(engine.IsQueuedForCheck(items[2].InfoHash));
        Assert.Equal("Очередь проверки", items[1].StatusText);
        Assert.Equal("Очередь проверки", items[2].StatusText);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && (engine.CurrentCheckingInfoHash is not null || engine.QueuedCheckCount > 0))
        {
            engine.AdvanceCheckQueue();
            await Task.Delay(25);
        }

        Assert.Null(engine.CurrentCheckingInfoHash);
        Assert.Equal(0, engine.QueuedCheckCount);
    }

    [Fact]
    public async Task LibtorrentEngine_CapturesBlockUploadedAlertsIntoUploadingPieces()
    {
        string seedAppData = Path.Combine(_tempDir, "seed_appdata");
        string leechAppData = Path.Combine(_tempDir, "leech_appdata");
        string seedDir = Path.Combine(_tempDir, "seed_files");
        string leechDir = Path.Combine(_tempDir, "leech_files");
        Directory.CreateDirectory(seedDir);
        Directory.CreateDirectory(leechDir);

        // Start a minimal in-process BitTorrent HTTP tracker on loopback so both engines meet reliably.
        using var trackerListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        trackerListener.Start();
        int trackerPort = ((System.Net.IPEndPoint)trackerListener.LocalEndpoint).Port;
        int seedPort = trackerPort + 101;
        int leechPort = trackerPort + 102;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var routableIfaces = LibtorrentEngineService.BuildRoutableListenInterfaces(seedPort);
        byte[] peerIpBytes = routableIfaces.OfType<TorrentSharp.Wrap.Utils.IpInterface>()
            .Select(i => i.Endpoint.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?
            .GetAddressBytes() ?? [127, 0, 0, 1];

        var peers = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();
        Task trackerLoop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = await trackerListener.AcceptTcpClientAsync(cts.Token);
                }
                catch
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        byte[] buf = new byte[2048];
                        int read = await stream.ReadAsync(buf, cts.Token);
                        string req = System.Text.Encoding.ASCII.GetString(buf, 0, read);
                        if (req.Contains("port=", StringComparison.Ordinal))
                        {
                            int idx = req.IndexOf("port=", StringComparison.Ordinal) + 5;
                            int end = idx;
                            while (end < req.Length && char.IsDigit(req[end])) end++;
                            if (int.TryParse(req.AsSpan(idx, end - idx), out int peerPort))
                                peers[peerPort] = true;
                        }

                        var compact = new List<byte>();
                        foreach (int p in peers.Keys)
                        {
                            compact.AddRange([peerIpBytes[0], peerIpBytes[1], peerIpBytes[2], peerIpBytes[3], (byte)(p >> 8), (byte)(p & 0xFF)]);
                        }

                        byte[] header = System.Text.Encoding.ASCII.GetBytes($"d8:intervali2e5:peers{compact.Count}:");
                        byte[] body = [.. header, .. compact, (byte)'e'];
                        string respHeader = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                        byte[] respBytes = [.. System.Text.Encoding.ASCII.GetBytes(respHeader), .. body];
                        await stream.WriteAsync(respBytes, cts.Token);
                    }
                }, cts.Token);
            }
        });

        string payloadFile = Path.Combine(seedDir, "upload_probe.bin");
        byte[] payload = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(payloadFile, payload);

        string trackerUrl = $"http://127.0.0.1:{trackerPort}/announce";
        var created = await BencodeHelper.CreateTorrentAsync(new TorrentCreationOptions(
            payloadFile,
            [trackerUrl],
            PieceLengthBytes: 64 * 1024));
        string torrentPath = Path.Combine(_tempDir, "upload_probe.torrent");
        await File.WriteAllBytesAsync(torrentPath, created.TorrentBytes);

        var seedSettings = new SettingsService(seedAppData);
        seedSettings.SaveSettings(new AppSettings
        {
            DefaultDownloadPath = seedDir,
            ListenPort = seedPort,
            EnableDht = false,
            EnableLocalPeerDiscovery = true,
            EnablePortForwarding = false
        });

        var leechSettings = new SettingsService(leechAppData);
        leechSettings.SaveSettings(new AppSettings
        {
            DefaultDownloadPath = leechDir,
            ListenPort = leechPort,
            EnableDht = false,
            EnableLocalPeerDiscovery = true,
            EnablePortForwarding = false
        });

        await using var seedEngine = new LibtorrentEngineService(seedSettings);
        await using var leechEngine = new LibtorrentEngineService(leechSettings);
        await seedEngine.InitializeAsync();
        await leechEngine.InitializeAsync();

        var seedItem = await seedEngine.AddTorrentFileAsync(torrentPath, seedDir, startImmediately: true);
        var leechItem = await leechEngine.AddTorrentFileAsync(torrentPath, leechDir, startImmediately: true);

        bool sawUploadedPiece = false;
        while (!cts.IsCancellationRequested)
        {
            await seedEngine.RefreshDetailsAsync(seedItem);
            await leechEngine.RefreshDetailsAsync(leechItem);
            if (seedItem.UploadingPieces.Count > 0)
            {
                sawUploadedPiece = true;
                break;
            }
            await Task.Delay(80, cts.Token);
        }

        cts.Cancel();
        trackerListener.Stop();
        try { await trackerLoop; } catch { }

        Assert.True(sawUploadedPiece,
            $"Expected seed engine to capture block_uploaded_alert into UploadingPieces. " +
            $"TrackerHits={peers.Count}, SeedState={seedItem.StatusText}, LeechState={leechItem.StatusText}, " +
            $"LeechDown={leechItem.DownloadedBytes}, SeedPeers={seedItem.Peers.Count}, LeechPeers={leechItem.Peers.Count}");
    }

    [Fact]
    public async Task LibtorrentEngine_CapturesBlockUploadedAlerts_WithCyrillicNameAndAfterApplySettings()
    {
        string seedAppData = Path.Combine(_tempDir, "seed_cyr_appdata");
        string leechAppData = Path.Combine(_tempDir, "leech_cyr_appdata");
        string seedDir = Path.Combine(_tempDir, "seed_cyr_files", "Образы Windows [10.0] [Ru]");
        string leechDir = Path.Combine(_tempDir, "leech_cyr_files");
        Directory.CreateDirectory(seedDir);
        Directory.CreateDirectory(leechDir);

        byte[] payload = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(seedDir, "data.bin"), payload);

        using var tracker = StartLoopbackTracker();
        var created = await BencodeHelper.CreateTorrentAsync(new TorrentCreationOptions(
            seedDir,
            [tracker.TrackerUrl],
            PieceLengthBytes: 64 * 1024));
        string torrentPath = Path.Combine(_tempDir, "cyr.torrent");
        await File.WriteAllBytesAsync(torrentPath, created.TorrentBytes);

        var seedSettings = new SettingsService(seedAppData);
        seedSettings.SaveSettings(new AppSettings
        {
            DefaultDownloadPath = Path.GetDirectoryName(seedDir)!,
            ListenPort = tracker.SeedPort,
            EnableDht = false,
            EnableLocalPeerDiscovery = false,
            EnablePortForwarding = false
        });
        var leechSettings = new SettingsService(leechAppData);
        leechSettings.SaveSettings(new AppSettings
        {
            DefaultDownloadPath = leechDir,
            ListenPort = tracker.LeechPort,
            EnableDht = false,
            EnableLocalPeerDiscovery = false,
            EnablePortForwarding = false
        });

        await using var seedEngine = new LibtorrentEngineService(seedSettings);
        await using var leechEngine = new LibtorrentEngineService(leechSettings);
        await seedEngine.InitializeAsync();
        await leechEngine.InitializeAsync();

        // Trigger ApplySettingsAsync like saving settings in UI
        await seedEngine.ApplySettingsAsync(seedSettings.Current);

        var seedItem = await seedEngine.AddTorrentFileAsync(torrentPath, Path.GetDirectoryName(seedDir)!, startImmediately: true);
        var leechItem = await leechEngine.AddTorrentFileAsync(torrentPath, leechDir, startImmediately: true);

        bool sawUploadedPiece = false;
        while (!tracker.CancellationToken.IsCancellationRequested)
        {
            await seedEngine.RefreshDetailsAsync(seedItem);
            await leechEngine.RefreshDetailsAsync(leechItem);
            if (seedItem.UploadingPieces.Count > 0)
            {
                sawUploadedPiece = true;
                break;
            }
            await Task.Delay(80, tracker.CancellationToken);
        }

        Assert.True(sawUploadedPiece, $"Cyrillic/ApplySettings failed to capture upload piece! SeedPeers={seedItem.Peers.Count}, LeechDown={leechItem.DownloadedBytes}");
    }

    [Fact]
    public void PieceMapControl_UsesLargerCellsAndFewerCellsToRender()
    {
        var (cols, rows, cellSize, _, _) = Gem.Controls.PieceMapControl.ComputeGridMetrics(840, 300);
        int totalCells = cols * rows;

        Assert.True(cellSize >= 12.0, $"Expected larger cellSize >= 12px, got {cellSize:F2}");
        Assert.True(totalCells < 1000, $"Expected fewer cells (< 1000), got {totalCells} ({cols}x{rows})");
    }

    [Fact]
    public void PieceMapControl_DrawsWhiteBorderOnUploadingSeedingCells()
    {
        Exception? staEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var control = new Gem.Controls.PieceMapControl
                {
                    Width = 840,
                    Height = 300,
                    IsSeeding = true,
                    TotalPieces = 8172,
                    Pieces = Enumerable.Repeat(1.0, 4096).ToArray(),
                    UploadingPieces = [100, 4000]
                };

                control.Measure(new System.Windows.Size(840, 300));
                control.Arrange(new System.Windows.Rect(0, 0, 840, 300));
                control.UpdateLayout();

                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    840,
                    300,
                    96,
                    96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(control);

                byte[] pixels = new byte[840 * 300 * 4];
                rtb.CopyPixels(pixels, 840 * 4, 0);

                bool foundPurple = false;
                bool foundBorderHighlight = false;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte b = pixels[i];
                    byte g = pixels[i + 1];
                    byte r = pixels[i + 2];
                    byte a = pixels[i + 3];
                    if (a == 0) continue;

                    if (r is >= 115 and <= 135 && g is >= 45 and <= 70 && b is >= 225 and <= 248)
                        foundPurple = true;

                    // White overlay blended over #7C3AED (0.25 initial opacity increases R,G,B above fill)
                    if (r > 138 && g > 72 && b >= 235)
                        foundBorderHighlight = true;
                }

                Assert.True(foundPurple, "Expected purple seeding cubes to be rendered.");
                Assert.True(foundBorderHighlight, "Expected border highlight around uploading seeding cells.");
            }
            catch (Exception ex)
            {
                staEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (staEx is not null)
            throw new Exception("STA WPF render test failed", staEx);
    }

    [Theory]
    [InlineData(0, false, 0.0)]
    [InlineData(452300, false, 45.23)]
    [InlineData(1000000, false, 100.0)]
    [InlineData(-50, false, 0.0)]
    [InlineData(1200000, false, 100.0)]
    [InlineData(0, true, 100.0)]
    public void ConvertPeerProgressPpmToPercent_ClampsAndRespectsSeederFlag(int ppm, bool isSeed, double expected)
    {
        double actual = LibtorrentEngineService.ConvertPeerProgressPpmToPercent(ppm, isSeed);
        Assert.Equal(expected, actual, precision: 4);
    }

    [Fact]
    public async Task LibtorrentEngine_ReportsPartialPeerProgressForNonSeederPeer()
    {
        string seedAppData = Path.Combine(_tempDir, "seed_p_appdata");
        string leechAppData = Path.Combine(_tempDir, "leech_p_appdata");
        string seedDir = Path.Combine(_tempDir, "seed_p_files");
        string leechDir = Path.Combine(_tempDir, "leech_p_files");
        string sourceFolder = Path.Combine(seedDir, "PartialAlbum");
        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(leechDir);

        byte[] partA = new byte[12 * 1024 * 1024];
        byte[] partB = new byte[12 * 1024 * 1024];
        Random.Shared.NextBytes(partA);
        Random.Shared.NextBytes(partB);
        await File.WriteAllBytesAsync(Path.Combine(sourceFolder, "part_a.bin"), partA);
        await File.WriteAllBytesAsync(Path.Combine(sourceFolder, "part_b.bin"), partB);

        using var tracker = StartLoopbackTracker();
        var created = await BencodeHelper.CreateTorrentAsync(new TorrentCreationOptions(
            sourceFolder,
            [tracker.TrackerUrl],
            PieceLengthBytes: 128 * 1024));
        string torrentPath = Path.Combine(_tempDir, "partial_probe.torrent");
        await File.WriteAllBytesAsync(torrentPath, created.TorrentBytes);

        var seedSettings = new SettingsService(seedAppData);
        seedSettings.SaveSettings(new AppSettings
        {
            DefaultDownloadPath = seedDir,
            ListenPort = tracker.SeedPort,
            EnableDht = false,
            EnableLocalPeerDiscovery = true,
            EnablePortForwarding = false
        });

        var leechSettings = new SettingsService(leechAppData);
        leechSettings.SaveSettings(new AppSettings
        {
            DefaultDownloadPath = leechDir,
            ListenPort = tracker.LeechPort,
            EnableDht = false,
            EnableLocalPeerDiscovery = true,
            EnablePortForwarding = false
        });

        await using var seedEngine = new LibtorrentEngineService(seedSettings);
        await using var leechEngine = new LibtorrentEngineService(leechSettings);
        await seedEngine.InitializeAsync();
        await leechEngine.InitializeAsync();

        var leechItem = await leechEngine.AddTorrentFileAsync(torrentPath, leechDir, startImmediately: true);
        await leechEngine.RefreshDetailsAsync(leechItem);
        Assert.True(leechItem.Files.Count >= 2);
        await leechEngine.SetFilePriorityAsync(leechItem, leechItem.Files[1], TorrentFilePriority.DoNotDownload);

        var seedItem = await seedEngine.AddTorrentFileAsync(torrentPath, seedDir, startImmediately: true);

        double? observedPartialProgress = null;
        try
        {
            while (!tracker.CancellationToken.IsCancellationRequested)
            {
                await seedEngine.RefreshDetailsAsync(seedItem);
                await leechEngine.RefreshDetailsAsync(leechItem);

                var peer = seedItem.Peers.FirstOrDefault(p => !p.IsSeeder && p.ProgressPercent > 5.0 && p.ProgressPercent < 95.0);
                if (peer is not null)
                {
                    observedPartialProgress = peer.ProgressPercent;
                    break;
                }

                await Task.Delay(25, tracker.CancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }

        string seedPeersDiag = string.Join(", ", seedItem.Peers.Select(p => $"{p.Endpoint}:seed={p.IsSeeder}:pct={p.ProgressPercent:F2}%:up={p.UploadSpeed}:dn={p.DownloadSpeed}"));
        string leechPeersDiag = string.Join(", ", leechItem.Peers.Select(p => $"{p.Endpoint}:seed={p.IsSeeder}:pct={p.ProgressPercent:F2}%:up={p.UploadSpeed}:dn={p.DownloadSpeed}"));
        string seedTrkDiag = string.Join(" | ", seedItem.Trackers.Select(t => $"{t.Uri}:{t.StatusLabel}:{t.FailureMessage}"));
        string leechTrkDiag = string.Join(" | ", leechItem.Trackers.Select(t => $"{t.Uri}:{t.StatusLabel}:{t.FailureMessage}"));
        string filesDiag = string.Join(";", leechItem.Files.Select(f => $"{f.FileIndex}:{f.RelativePath}:{f.Priority}:{f.Length}"));
        Assert.True(observedPartialProgress.HasValue,
            $"Expected non-seeder peer progress in (5,95). TrackerPorts=[{string.Join(",", tracker.AnnouncedPorts)}] (seed={tracker.SeedPort},leech={tracker.LeechPort}), " +
            $"SeedState={seedItem.StatusText}({seedItem.ErrorMessage}), LeechState={leechItem.StatusText}({leechItem.ErrorMessage}), LeechPct={leechItem.Progress:F2}%, LeechDown={leechItem.DownloadedBytes}, " +
            $"SeedPeers=[{seedPeersDiag}], LeechPeers=[{leechPeersDiag}], Files=[{filesDiag}], SeedTrk=[{seedTrkDiag}], LeechTrk=[{leechTrkDiag}]");
        Assert.InRange(observedPartialProgress!.Value, 5.0, 95.0);
    }

    private static LoopbackTrackerHandle StartLoopbackTracker()
    {
        var routableIfaces = LibtorrentEngineService.BuildRoutableListenInterfaces(0);
        var peerIp = routableIfaces.OfType<TorrentSharp.Wrap.Utils.IpInterface>()
            .Select(i => i.Endpoint.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?? System.Net.IPAddress.Loopback;
        byte[] peerIpBytes = peerIp.GetAddressBytes();

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 0);
        listener.Start();
        int trackerPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        int seedPort = trackerPort + 101;
        int leechPort = trackerPort + 102;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var peers = new System.Collections.Concurrent.ConcurrentDictionary<int, bool>();
        var loopTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cts.Token);
                }
                catch
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        byte[] buf = new byte[2048];
                        int read = await stream.ReadAsync(buf, cts.Token);
                        string req = System.Text.Encoding.ASCII.GetString(buf, 0, read);
                        int callerPort = 0;
                        if (req.Contains("port=", StringComparison.Ordinal))
                        {
                            int idx = req.IndexOf("port=", StringComparison.Ordinal) + 5;
                            int end = idx;
                            while (end < req.Length && char.IsDigit(req[end])) end++;
                            if (int.TryParse(req.AsSpan(idx, end - idx), out int peerPort))
                            {
                                callerPort = peerPort;
                                peers[peerPort] = true;
                            }
                        }

                        var compact = new List<byte>();
                        foreach (int p in peers.Keys)
                        {
                            if (p == callerPort) continue;
                            compact.AddRange([peerIpBytes[0], peerIpBytes[1], peerIpBytes[2], peerIpBytes[3], (byte)(p >> 8), (byte)(p & 0xFF)]);
                        }

                        byte[] header = System.Text.Encoding.ASCII.GetBytes($"d8:intervali2e5:peers{compact.Count}:");
                        byte[] body = [.. header, .. compact, (byte)'e'];
                        string respHeader = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                        byte[] respBytes = [.. System.Text.Encoding.ASCII.GetBytes(respHeader), .. body];
                        await stream.WriteAsync(respBytes, cts.Token);
                    }
                }, cts.Token);
            }
        });

        return new LoopbackTrackerHandle(listener, cts, loopTask, $"http://{peerIp}:{trackerPort}/announce", seedPort, leechPort, peers);
    }

    [Fact]
    public async Task HandleDownloadCompletedTransition_RespectsSeedOverrideAndFiresPostActionOnce()
    {
        string appData = Path.Combine(_tempDir, "appdata_completion");
        var settings = new SettingsService(appData);
        settings.SaveSettings(new AppSettings
        {
            SeedAfterDownload = false
        });

        var power = new FakeSystemPowerService();
        await using var engine = new LibtorrentEngineService(settings, power);

        var torrentNoSeed = new TorrentItemModel
        {
            InfoHash = "aaa111",
            Name = "Archive",
            PostCompletionAction = PostCompletionAction.Hibernate
        };

        bool stopCalled = false;
        bool stopped = engine.HandleDownloadCompletedTransition(torrentNoSeed, () => stopCalled = true);
        Assert.True(stopped);
        Assert.True(stopCalled);
        Assert.Equal(PostCompletionAction.None, torrentNoSeed.PostCompletionAction);
        Assert.Equal([PostCompletionAction.Hibernate], power.ExecutedActions);

        // Second call does not re-trigger cleared post action
        engine.HandleDownloadCompletedTransition(torrentNoSeed, null);
        Assert.Single(power.ExecutedActions);

        // Override=true seeds even when global SeedAfterDownload=false
        var torrentForceSeed = new TorrentItemModel
        {
            InfoHash = "bbb222",
            Name = "LinuxISO",
            SeedAfterDownloadOverride = true
        };
        bool forcedStopCalled = false;
        bool stopped2 = engine.HandleDownloadCompletedTransition(torrentForceSeed, () => forcedStopCalled = true);
        Assert.False(stopped2);
        Assert.False(forcedStopCalled);
    }

    private sealed class FakeSystemPowerService : ISystemPowerService
    {
        public List<PostCompletionAction> ExecutedActions { get; } = [];

        public bool Execute(PostCompletionAction action)
        {
            ExecutedActions.Add(action);
            return true;
        }
    }

    private sealed class LoopbackTrackerHandle : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly CancellationTokenSource _cts;
        private readonly Task _loopTask;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _peers;

        public LoopbackTrackerHandle(
            System.Net.Sockets.TcpListener listener,
            CancellationTokenSource cts,
            Task loopTask,
            string trackerUrl,
            int seedPort,
            int leechPort,
            System.Collections.Concurrent.ConcurrentDictionary<int, bool> peers)
        {
            _listener = listener;
            _cts = cts;
            _loopTask = loopTask;
            TrackerUrl = trackerUrl;
            SeedPort = seedPort;
            LeechPort = leechPort;
            _peers = peers;
        }

        public string TrackerUrl { get; }
        public int SeedPort { get; }
        public int LeechPort { get; }
        public int PeerCount => _peers.Count;
        public IEnumerable<int> AnnouncedPorts => _peers.Keys.OrderBy(x => x);
        public CancellationToken CancellationToken => _cts.Token;

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _loopTask.Wait(1000); } catch { }
            _cts.Dispose();
        }
    }
}
