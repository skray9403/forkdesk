using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TwinDesk.Native.Core;

/// <summary>
/// Protocol-v1 pairing cryptography. The byte ordering and input encoding match
/// shared/protocol.js and Android Proto.kt; changing these values breaks pairing.
/// </summary>
public static class ProtocolCrypto
{
    private static readonly byte[] SasLabel = Encoding.UTF8.GetBytes(ProtocolV1.SasLabel);

    public static string RandomHex(int byteCount)
    {
        if (byteCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(byteCount), "Byte count must be positive.");

        return Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();
    }

    public static string Sha256Hex(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    /// <summary>SHA-256 of the decoded nonce bytes, not of the nonce's hexadecimal text.</summary>
    public static string CommitOf(string nonceHex)
    {
        var nonce = ParseHex(nonceHex, nameof(nonceHex), expectedBytes: 16);
        return Convert.ToHexString(SHA256.HashData(nonce)).ToLowerInvariant();
    }

    /// <summary>
    /// Six-digit short authentication string:
    /// SHA-256(label UTF-8 || fingerprint bytes || Windows nonce bytes || Android nonce bytes),
    /// first four digest bytes interpreted as unsigned big-endian, modulo one million.
    /// </summary>
    public static string SasCode(string fingerprint, string windowsNonceHex, string androidNonceHex)
    {
        var fingerprintBytes = ParseHex(NormalizeFingerprint(fingerprint), nameof(fingerprint), expectedBytes: 32);
        var windowsNonce = ParseHex(windowsNonceHex, nameof(windowsNonceHex), expectedBytes: 16);
        var androidNonce = ParseHex(androidNonceHex, nameof(androidNonceHex), expectedBytes: 16);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(SasLabel);
        hash.AppendData(fingerprintBytes);
        hash.AppendData(windowsNonce);
        hash.AppendData(androidNonce);
        var digest = hash.GetHashAndReset();
        var number = BinaryPrimitives.ReadUInt32BigEndian(digest.AsSpan(0, sizeof(uint)));
        return (number % 1_000_000U).ToString("D6", CultureInfo.InvariantCulture);
    }

    public static string NormalizeFingerprint(string? fingerprint) =>
        (fingerprint ?? string.Empty).Replace(":", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

    /// <summary>Compares a token with its stored SHA-256 hex digest in constant time.</summary>
    public static bool TokenMatches(string? token, string? expectedHashHex)
    {
        if (token is null || expectedHashHex is null)
            return false;

        byte[] expected;
        try
        {
            expected = ParseHex(expectedHashHex, nameof(expectedHashHex), expectedBytes: 32);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] ParseHex(string value, string parameterName, int expectedBytes)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != expectedBytes * 2)
            throw new FormatException($"{parameterName} must contain exactly {expectedBytes * 2} hexadecimal characters.");

        try
        {
            return Convert.FromHexString(value);
        }
        catch (FormatException ex)
        {
            throw new FormatException($"{parameterName} contains invalid hexadecimal characters.", ex);
        }
    }
}
