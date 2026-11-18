using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace TwinDesk.Native.Core;

/// <summary>
/// A stable Windows host identity. The certificate's SHA-256 fingerprint is
/// the exact DER certificate fingerprint used by the existing protocol-v1 peers.
/// </summary>
public sealed class NativeIdentity : IDisposable
{
    public string Id { get; }
    public string Name { get; }
    public string Fingerprint { get; }
    public X509Certificate2 Certificate { get; }

    internal NativeIdentity(string id, string name, X509Certificate2 certificate)
    {
        Id = id;
        Name = name;
        Certificate = certificate;
        Fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
    }

    public void Dispose() => Certificate.Dispose();
}

/// <summary>
/// Persists the device UUID and a self-signed TLS certificate under the native
/// app-data directory. The PFX password is protected with Windows DPAPI
/// (CurrentUser scope), matching the legacy Windows client's protection model.
/// </summary>
public sealed class NativeIdentityStore
{
    private const string CertificateSubject = "CN=MacShare";
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private readonly string _rootDirectory;
    private readonly object _sync = new();

    public NativeIdentityStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public NativeIdentity LoadOrCreate(string? deviceName = null)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(_rootDirectory);
            var id = LoadOrCreateDeviceId();
            var name = string.IsNullOrWhiteSpace(deviceName)
                ? Environment.MachineName
                : deviceName.Trim();

            var identityDirectory = Path.Combine(_rootDirectory, "identity");
            Directory.CreateDirectory(identityDirectory);
            var pfxPath = Path.Combine(identityDirectory, "identity.pfx");
            var passPath = Path.Combine(identityDirectory, "identity.pass");

            byte[] pfxBytes;
            string passphrase;
            if (File.Exists(pfxPath) && File.Exists(passPath))
            {
                var protectedPassphrase = File.ReadAllBytes(passPath);
                var plaintext = DpapiSecretProtector.Unprotect(protectedPassphrase);
                try
                {
                    passphrase = Encoding.UTF8.GetString(plaintext);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }

                pfxBytes = File.ReadAllBytes(pfxPath);
            }
            else
            {
                passphrase = ProtocolCrypto.RandomHex(24);
                using var generated = CreateCertificate();
                pfxBytes = generated.Export(X509ContentType.Pfx, passphrase);
                WriteAtomically(pfxPath, pfxBytes);

                var passBytes = Encoding.UTF8.GetBytes(passphrase);
                try
                {
                    WriteAtomically(passPath, DpapiSecretProtector.Protect(passBytes));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(passBytes);
                }
            }

            var certificate = X509CertificateLoader.LoadPkcs12(
                pfxBytes,
                passphrase,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);

            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new CryptographicException("The persisted host certificate has no private key.");
            }

            return new NativeIdentity(id, name, certificate);
        }
    }

    private string LoadOrCreateDeviceId()
    {
        var devicePath = Path.Combine(_rootDirectory, "device.json");
        if (File.Exists(devicePath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(devicePath));
                if (document.RootElement.TryGetProperty("id", out var idElement) &&
                    idElement.ValueKind == JsonValueKind.String)
                {
                    var existing = idElement.GetString();
                    if (Guid.TryParse(existing, out _) && !string.IsNullOrWhiteSpace(existing))
                        return existing;
                }
            }
            catch (JsonException)
            {
                // A damaged device record is replaced with a new stable identity.
            }
        }

        var id = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { id });
        WriteAtomically(devicePath, payload);
        return id;
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            CertificateSubject,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: false));

        var usages = new OidCollection { new(ServerAuthenticationOid) };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(10));
    }

    private static void WriteAtomically(string path, byte[] contents)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, contents);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

}

/// <summary>DPAPI CurrentUser protector for small secrets such as the PFX password.</summary>
internal static class DpapiSecretProtector
{
    private const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Transform(plaintext, protect: true);
    }

    public static byte[] Unprotect(byte[] protectedData)
    {
        ArgumentNullException.ThrowIfNull(protectedData);
        return Transform(protectedData, protect: false);
    }

    private static byte[] Transform(byte[] inputBytes, bool protect)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native identity secrets require Windows DPAPI.");

        var input = new DataBlob
        {
            Length = inputBytes.Length,
            Data = Marshal.AllocHGlobal(inputBytes.Length)
        };
        Marshal.Copy(inputBytes, 0, input.Data, inputBytes.Length);

        DataBlob output = default;
        try
        {
            var success = protect
                ? CryptProtectData(ref input, "TwinDesk native identity", IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, UiForbidden, out output);

            if (!success)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    protect ? "Could not protect the TwinDesk identity secret." :
                        "Could not unprotect the TwinDesk identity secret.");

            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
                LocalFree(output.Data);
        }
    }
}
