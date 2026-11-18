using Gem.Services;

namespace Gem.Tests;

public sealed class SingleInstanceServiceTests
{
    [Fact]
    public async Task PrimaryInstance_BlocksSecondInstance_AndReceivesForwardedMagnetAndTorrentArgs()
    {
        string scope = $"GemUnitTest_{Guid.NewGuid():N}";
        using var primary = new SingleInstanceService(scope);
        using var secondary = new SingleInstanceService(scope);

        Assert.True(primary.TryAcquirePrimary());
        Assert.False(secondary.TryAcquirePrimary());

        var receivedTcs = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(args => receivedTcs.TrySetResult(args));

        string[] forwarded =
        [
            "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567&dn=Ubuntu",
            @"D:\Downloads\release.torrent"
        ];

        bool sent = secondary.NotifyPrimaryInstance(forwarded, TimeSpan.FromSeconds(3));
        Assert.True(sent);

        string[] received = await receivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(forwarded, received);
    }
}
