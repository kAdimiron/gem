using Gem.Services;
using Microsoft.Win32;

namespace Gem.Tests;

public sealed class StartupServiceTests : IDisposable
{
    private readonly string _testSubKeyPath = $@"Software\GemTests_Startup_{Guid.NewGuid():N}";

    [Fact]
    public void SetStartupEnabled_RegistersAndRemovesQuotedExecutablePathInRegistry()
    {
        const string fakeExe = @"C:\Program Files\Gem\Gem.exe";
        var service = new WindowsStartupService(_testSubKeyPath, "Gem", () => fakeExe);

        Assert.False(service.IsStartupEnabled());

        Assert.True(service.SetStartupEnabled(true));
        Assert.True(service.IsStartupEnabled());

        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(_testSubKeyPath, writable: false))
        {
            Assert.NotNull(key);
            Assert.Equal($"\"{fakeExe}\"", key!.GetValue("Gem"));
        }

        Assert.True(service.SetStartupEnabled(false));
        Assert.False(service.IsStartupEnabled());
    }

    [Fact]
    public void SetStartupEnabled_ReturnsFalseWhenExecutablePathIsUnavailable()
    {
        var service = new WindowsStartupService(_testSubKeyPath, "Gem", () => null);

        Assert.False(service.SetStartupEnabled(true));
        Assert.False(service.IsStartupEnabled());
    }

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_testSubKeyPath, throwOnMissingSubKey: false);
        }
        catch
        {
        }
    }
}
