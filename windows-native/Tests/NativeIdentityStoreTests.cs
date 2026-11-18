using System.Security.Cryptography;
using TwinDesk.Native.Core;
using Xunit;

namespace TwinDesk.Native.Tests;

public sealed class NativeIdentityStoreTests
{
    [Fact]
    public void IdentityAndCertificateRemainStableAcrossLoads()
    {
        var root = Path.Combine(Path.GetTempPath(), "TwinDesk.Native.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var first = new NativeIdentityStore(root).LoadOrCreate("Test Windows PC");
            using var second = new NativeIdentityStore(root).LoadOrCreate("Test Windows PC");

            Assert.Equal(first.Id, second.Id);
            Assert.Equal(first.Name, second.Name);
            Assert.Equal(first.Fingerprint, second.Fingerprint);
            Assert.True(first.Certificate.HasPrivateKey);
            Assert.Equal(64, first.Fingerprint.Length);
            Assert.Matches("^[0-9a-f]{64}$", first.Fingerprint);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CertificateFingerprintIsSha256OfDerCertificate()
    {
        var root = Path.Combine(Path.GetTempPath(), "TwinDesk.Native.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var identity = new NativeIdentityStore(root).LoadOrCreate("Test Windows PC");
            var expected = Convert.ToHexString(SHA256.HashData(identity.Certificate.RawData)).ToLowerInvariant();

            Assert.Equal(expected, identity.Fingerprint);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
