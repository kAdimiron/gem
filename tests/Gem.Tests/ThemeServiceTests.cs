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
    [InlineData(AppColorTheme.Cobalt, 0x25, 0x63, 0xEB, 0x1E, 0x40, 0xAF)]
    [InlineData(AppColorTheme.Cyan, 0x08, 0x91, 0xB2, 0x0E, 0x74, 0x90)]
    [InlineData(AppColorTheme.Amber, 0xD9, 0x77, 0x06, 0xB4, 0x53, 0x09)]
    [InlineData(AppColorTheme.Rose, 0xE1, 0x1D, 0x48, 0x9F, 0x12, 0x39)]
    [InlineData(AppColorTheme.Orchid, 0xC0, 0x26, 0xD3, 0x86, 0x19, 0x8F)]
    [InlineData(AppColorTheme.Forest, 0x16, 0xA3, 0x4A, 0x15, 0x80, 0x3D)]
    [InlineData(AppColorTheme.Gold, 0xEA, 0xB3, 0x08, 0xA1, 0x62, 0x07)]
    [InlineData(AppColorTheme.Slate, 0x64, 0x74, 0x8B, 0x33, 0x41, 0x55)]
    [InlineData(AppColorTheme.Indigo, 0x4F, 0x46, 0xE5, 0x0E, 0xA5, 0xE9)]
    [InlineData(AppColorTheme.Coral, 0xFF, 0x6F, 0x61, 0xC2, 0x18, 0x5B)]
    [InlineData(AppColorTheme.Sakura, 0xF4, 0x8F, 0xB1, 0xAD, 0x14, 0x57)]
    [InlineData(AppColorTheme.Mocha, 0x8B, 0x5E, 0x3C, 0x5D, 0x3A, 0x1A)]
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
