using TwinDesk.Native.Core;
using Xunit;

namespace TwinDesk.Native.Tests;

public sealed class ProtocolCryptoTests
{
    private const string Fingerprint = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
    private const string WindowsNonce = "000102030405060708090a0b0c0d0e0f";
    private const string AndroidNonce = "101112131415161718191a1b1c1d1e1f";

    [Fact]
    public void CommitMatchesProtocolV1ByteBasedSha256Vector()
    {
        Assert.Equal(
            "be45cb2605bf36bebde684841a28f0fd43c69850a3dce5fedba69928ee3a8991",
            ProtocolCrypto.CommitOf(WindowsNonce));
    }

    [Fact]
    public void SasMatchesProtocolV1BigEndianVector()
    {
        Assert.Equal("187236", ProtocolCrypto.SasCode(Fingerprint, WindowsNonce, AndroidNonce));
    }

    [Fact]
    public void SasAcceptsColonSeparatedUppercaseCertificateFingerprint()
    {
        var formatted = string.Join(":", Enumerable.Range(0, 32).Select(i => i.ToString("x2").ToUpperInvariant()));
        Assert.Equal("187236", ProtocolCrypto.SasCode(formatted, WindowsNonce, AndroidNonce));
    }

    [Fact]
    public void TokenMatchUsesUtf8Sha256Digest()
    {
        const string token = "twindesk-test-token";
        const string hash = "202aa78fa7fb141096509ba91f341f94c1727a1a9ffc796d7d4708e7f8b0f907";

        Assert.True(ProtocolCrypto.TokenMatches(token, hash));
        Assert.False(ProtocolCrypto.TokenMatches(token + "x", hash));
        Assert.False(ProtocolCrypto.TokenMatches(token, "not-a-hash"));
        Assert.False(ProtocolCrypto.TokenMatches(null, hash));
    }

    [Fact]
    public void RandomHexReturnsRequestedLowercaseByteLength()
    {
        var value = ProtocolCrypto.RandomHex(16);

        Assert.Equal(32, value.Length);
        Assert.Matches("^[0-9a-f]{32}$", value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("xyz")]
    [InlineData("000102")]
    public void CommitRejectsMalformedOrWrongLengthNonce(string nonce)
    {
        Assert.Throws<FormatException>(() => ProtocolCrypto.CommitOf(nonce));
    }
}
