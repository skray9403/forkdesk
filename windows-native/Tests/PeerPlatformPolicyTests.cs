using TwinDesk.Native.Core;
using Xunit;

namespace TwinDesk.Native.Tests;

public sealed class PeerPlatformPolicyTests
{
    [Theory]
    [InlineData("android", PeerPlatform.Android)]
    [InlineData("win", PeerPlatform.Windows)]
    [InlineData("windows", PeerPlatform.Windows)]
    public void AcceptsSupportedWirePeers(string wireId, PeerPlatform expected)
    {
        Assert.True(PeerPlatformPolicy.TryParseWireId(wireId, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("mac")]
    [InlineData("linux")]
    [InlineData("ios")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsUnsupportedWirePeers(string? wireId)
    {
        Assert.False(PeerPlatformPolicy.TryParseWireId(wireId, out var actual));
        Assert.Equal(PeerPlatform.Unknown, actual);
    }
}
