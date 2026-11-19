using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TwinDesk.Native.Core;

public sealed record FirewallAccessStatus(bool IsAllowed, string Message);

/// <summary>
/// Checks and repairs only the inbound rules required by protocol v1:
/// TCP 47800 for pinned TLS/WebSocket and UDP 47801 for peer discovery.
/// Repair is explicit and asks Windows for administrator approval.
/// </summary>
public sealed class NativeFirewallAccessService
{
    public FirewallAccessStatus Check(string? executablePath = null)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, "Windows Firewall status is available only on Windows.");

        executablePath ??= Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            return new(false, "Could not determine the TwinDesk executable path.");

        try
        {
            return CheckWindows(Path.GetFullPath(executablePath));
        }
        catch (Exception ex)
        {
            return new(false, $"Could not verify Windows Firewall rules: {ex.Message}");
        }
    }

    public async Task<FirewallAccessStatus> EnsureAsync(
        string applicationDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Firewall repair is available only on Windows.");

        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        var executablePath = Environment.ProcessPath;
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var script = Path.Combine(applicationDirectory, "Firewall", "AllowFirewall.ps1");
        if (!File.Exists(script))
            throw new FileNotFoundException("The TwinDesk firewall repair script was not installed.", script);

        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

        var startInfo = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
                Quote(script) + " -Exe " + Quote(Path.GetFullPath(executablePath))
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the elevated firewall repair process.");

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Windows Firewall repair exited with code {process.ExitCode}.");

        return Check(executablePath);
    }

    [SupportedOSPlatform("windows")]
    private static FirewallAccessStatus CheckWindows(string executablePath)
    {
        object? policyObject = null;
        object? rulesObject = null;
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
                ?? throw new COMException("Windows Firewall policy API is unavailable.");
            policyObject = Activator.CreateInstance(policyType)
                ?? throw new COMException("Could not open Windows Firewall policy.");

            dynamic policy = policyObject;
            rulesObject = policy.Rules;
            var currentProfiles = Convert.ToInt32(policy.CurrentProfileTypes, CultureInfo.InvariantCulture);
            if (currentProfiles == 0)
                currentProfiles = int.MaxValue;

            var hasTcp = HasAllowRule(rulesObject, executablePath, 6, ProtocolV1.Port, currentProfiles);
            var hasUdp = HasAllowRule(rulesObject, executablePath, 17, ProtocolV1.DiscoveryPort, currentProfiles);

            return hasTcp && hasUdp
                ? new(true, "Windows Firewall allows TwinDesk TLS pairing and UDP discovery.")
                : new(false, "Windows Firewall needs app-specific inbound rules for TCP 47800 and UDP 47801.");
        }
        finally
        {
            if (rulesObject is not null && Marshal.IsComObject(rulesObject))
                Marshal.FinalReleaseComObject(rulesObject);
            if (policyObject is not null && Marshal.IsComObject(policyObject))
                Marshal.FinalReleaseComObject(policyObject);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasAllowRule(object rulesObject, string executablePath,
        int protocol, int port, int activeProfiles)
    {
        var enumerator = ((IEnumerable)rulesObject).GetEnumerator();
        try
        {
            while (enumerator.MoveNext())
            {
                if (enumerator.Current is not object ruleObject)
                    continue;

                dynamic rule = ruleObject;
                if (!Convert.ToBoolean(rule.Enabled, CultureInfo.InvariantCulture) ||
                    Convert.ToInt32(rule.Direction, CultureInfo.InvariantCulture) != 1 ||
                    Convert.ToInt32(rule.Action, CultureInfo.InvariantCulture) != 1 ||
                    Convert.ToInt32(rule.Protocol, CultureInfo.InvariantCulture) != protocol)
                    continue;

                var ruleProtocol = Convert.ToInt32(rule.Protocol, CultureInfo.InvariantCulture);
                // 256 is NET_FW_IP_PROTOCOL_ANY, 0 is any, or matches specific protocol
                if (ruleProtocol != 256 && ruleProtocol != 0 && ruleProtocol != protocol)
                    continue;

                var program = Convert.ToString(rule.ApplicationName, CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(program) ||
                    !string.Equals(Path.GetFullPath(program), executablePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var profiles = Convert.ToInt32(rule.Profiles, CultureInfo.InvariantCulture);
                // 2147483647 (int.MaxValue) is NET_FW_PROFILE2_ALL; otherwise must match at least one active profile bit
                if (profiles != int.MaxValue && (profiles & activeProfiles) == 0)
                    continue;

                string localPorts = Convert.ToString((object?)rule.LocalPorts, CultureInfo.InvariantCulture) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(localPorts) || localPorts == "*" ||
                    localPorts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Any(value => value == port.ToString(CultureInfo.InvariantCulture)))
                    return true;
            }
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }

        return false;
    }

    private static string Quote(string value) => ((char)34) + value + ((char)34);
}
