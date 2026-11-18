using System.Windows.Media;
using Gem.Services;

namespace Gem.Tests;

public sealed class ThemeServiceTests
{
    [Fact]
    public void ThemeService_UsesFixedBrandAccentColor_IndependentOfWindowsPersonalization()
    {
        var service = new ThemeService();

        Assert.Equal(Color.FromRgb(0x01, 0x85, 0x74), ThemeService.FixedAccentColor);
        Assert.Equal(ThemeService.FixedAccentColor, service.CurrentAccentColor);

        service.ApplyFixedAccent();

        Assert.Equal(Color.FromRgb(0x01, 0x85, 0x74), service.CurrentAccentColor);
    }
}
