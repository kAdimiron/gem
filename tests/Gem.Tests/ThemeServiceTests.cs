using System.Windows.Media;
using Gem.Models;
using Gem.Services;

namespace Gem.Tests;

public sealed class ThemeServiceTests
{
    [Fact]
    public void ThemeService_UsesFixedBrandAccentColor_IndependentOfWindowsPersonalization()
    {
        var service = new ThemeService();

        Assert.Equal(AppColorTheme.Mint, service.CurrentColorTheme);
        Assert.Equal(Color.FromRgb(0x01, 0x85, 0x74), ThemeService.FixedAccentColor);
        Assert.Equal(ThemeService.FixedAccentColor, service.CurrentAccentColor);
        Assert.Equal(Color.FromRgb(0x10, 0x9E, 0x4E), service.CurrentSeedingAccentColor);

        service.ApplyFixedAccent();

        Assert.Equal(Color.FromRgb(0x01, 0x85, 0x74), service.CurrentAccentColor);
    }

    [Theory]
    [InlineData(AppColorTheme.Mint, 0x01, 0x85, 0x74, 0x10, 0x9E, 0x4E)]
    [InlineData(AppColorTheme.Tomato, 0xDC, 0x3E, 0x26, 0xEA, 0x73, 0x17)]
    [InlineData(AppColorTheme.Purple, 0x7C, 0x3A, 0xED, 0x5B, 0x21, 0xB6)]
    [InlineData(AppColorTheme.Sky, 0x8D, 0xBC, 0xD8, 0x1D, 0x6F, 0x96)]
    public void ThemeService_AppliesExpectedTwoColorPalettes(
        AppColorTheme palette,
        byte pr, byte pg, byte pb,
        byte sr, byte sg, byte sb)
    {
        var service = new ThemeService();
        service.ApplyFixedAccent(colorTheme: palette);

        Assert.Equal(palette, service.CurrentColorTheme);
        Assert.Equal(Color.FromRgb(pr, pg, pb), service.CurrentAccentColor);
        Assert.Equal(Color.FromRgb(sr, sg, sb), service.CurrentSeedingAccentColor);
    }
}
