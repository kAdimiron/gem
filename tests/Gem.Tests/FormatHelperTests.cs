using System.Globalization;
using System.Windows.Media;
using Gem.Converters;
using Gem.Helpers;
using Gem.Models;
using Gem.Services;

namespace Gem.Tests;

[Collection("Localization")]
public class FormatHelperTests
{
    public FormatHelperTests()
    {
        LocalizationService.Instance.SetLanguage("ru-RU");
    }

    [Theory]
    [InlineData(0, "0 Б")]
    [InlineData(512, "512 Б")]
    [InlineData(1024, "1 КБ")]
    [InlineData(1536, "1.5 КБ")]
    [InlineData(1048576, "1 МБ")]
    [InlineData(1073741824, "1 ГБ")]
    public void FormatBytes_FormatsHumanReadableUnits(long bytes, string expected)
    {
        Assert.Equal(expected, FormatHelper.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(0, "0 Б/с")]
    [InlineData(2048, "2 КБ/с")]
    [InlineData(2097152, "2 МБ/с")]
    public void FormatSpeed_FormatsHumanReadableSpeeds(long bytesPerSec, string expected)
    {
        Assert.Equal(expected, FormatHelper.FormatSpeed(bytesPerSec));
    }

    [Fact]
    public void FormatEta_HandlesNullAndDurations()
    {
        Assert.Equal("∞", FormatHelper.FormatEta(null));
        Assert.Equal("45с", FormatHelper.FormatEta(TimeSpan.FromSeconds(45)));
        Assert.Equal("3м 10с", FormatHelper.FormatEta(TimeSpan.FromSeconds(190)));
        Assert.Equal("2ч 5м", FormatHelper.FormatEta(TimeSpan.FromMinutes(125)));
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:ABCDEF1234567890ABCDEF1234567890ABCDEF12&dn=Ubuntu", true)]
    [InlineData("MAGNET:?xt=urn:btih:123456", true)]
    [InlineData("https://example.com/file.torrent", false)]
    [InlineData("ubuntu iso", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsMagnetUri_ValidatesMagnetLinks(string? input, bool expected)
    {
        Assert.Equal(expected, FormatHelper.IsMagnetUri(input));
    }

    [Fact]
    public void CalculateEta_ComputesRemainingTime()
    {
        var eta = FormatHelper.CalculateEta(10_000, 4_000, 2_000);
        Assert.Equal(TimeSpan.FromSeconds(3), eta);
        Assert.Null(FormatHelper.CalculateEta(10_000, 4_000, 0));
        Assert.Equal(TimeSpan.Zero, FormatHelper.CalculateEta(10_000, 10_000, 1_000));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567", "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("btih:0123456789abcdef0123456789abcdef01234567", "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("urn:btih:0123456789abcdef0123456789abcdef01234567", "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567", "magnet:?xt=urn:btih:ABCDEFGHIJKLMNOPQRSTUVWXYZ234567")]
    [InlineData("magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567&dn=Ubuntu", "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567&dn=Ubuntu")]
    [InlineData("not-a-hash", null)]
    [InlineData("", null)]
    public void NormalizeToMagnetUri_HandlesMagnetsAndInfoHashes(string input, string? expected)
    {
        Assert.Equal(expected, FormatHelper.NormalizeToMagnetUri(input));
    }

    [Theory]
    [InlineData("ipv4://93.123.241.212:49653/", "93.123.241.212:49653")]
    [InlineData("ipv6://[2001:db8::1]:6881/", "[2001:db8::1]:6881")]
    [InlineData("tcp://77.68.41.117:51500/", "77.68.41.117:51500")]
    [InlineData("93.123.241.212:49653", "93.123.241.212:49653")]
    public void FormatPeerEndpoint_StripsSchemeAndSlashes(string raw, string expected)
    {
        Assert.Equal(expected, FormatHelper.FormatPeerEndpoint(new Uri(raw.Contains("://") ? raw : $"ipv4://{raw}")));
        Assert.Equal(expected, FormatHelper.FormatPeerEndpoint(raw));
    }

    [Fact]
    public void ParseTrackerList_ValidatesAndDeduplicatesUrls()
    {
        string raw = """
            # comment line
            udp://tracker.opentrackr.org:1337/announce

            https://tracker.example.com/announce
            UDP://TRACKER.OPENTRACKR.ORG:1337/announce
            ftp://invalid-scheme.example.com/announce
            not a url
            """;

        var list = FormatHelper.ParseTrackerList(raw);
        Assert.Equal(2, list.Count);
        Assert.Equal("udp://tracker.opentrackr.org:1337/announce", list[0]);
        Assert.Equal("https://tracker.example.com/announce", list[1]);
    }

    [Fact]
    public void StatusKindToBrushConverter_UsesDistinctThemeColorsForDownloadingAndSeeding()
    {
        var converter = new StatusKindToBrushConverter();
        var seedingBrush = Assert.IsType<SolidColorBrush>(converter.Convert(TorrentStatusKind.Seeding, typeof(Brush), null!, CultureInfo.InvariantCulture));
        var downloadingBrush = Assert.IsType<SolidColorBrush>(converter.Convert(TorrentStatusKind.Downloading, typeof(Brush), null!, CultureInfo.InvariantCulture));

        Assert.Equal(Color.FromRgb(0x01, 0x85, 0x74), downloadingBrush.Color);
        Assert.Equal(Color.FromRgb(0x10, 0x9E, 0x4E), seedingBrush.Color);
        Assert.NotEqual(downloadingBrush.Color, seedingBrush.Color);
    }
}
