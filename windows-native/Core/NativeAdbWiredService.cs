using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TwinDesk.Native.Core;

public sealed record AdbWiredSnapshot(
    bool IsAvailable,
    string? AdbPath,
    IReadOnlyList<string> ConnectedSerials,
    string Status);

/// <summary>
/// Native supervisor for USB/ADB wired connections to Android devices.
/// Replicates the zero-overhead behavior of legacy wired.js:
/// 1. Discovers attached USB Android devices via adb devices.
/// 2. Configures 'adb reverse tcp:47800 tcp:47800' to route session traffic.
/// 3. Broadcasts to com.twindesk.android/.WiredReceiver so the phone immediately
///    connects with ultra-low latency or auto-initiates SAS pairing.
/// </summary>
public sealed class NativeAdbWiredService : IAsyncDisposable
{
    private const string TargetAppId = "com.twindesk.android";
    private readonly string _hostId;
    private readonly string _hostName;
    private readonly int _port;
    private readonly Func<bool> _isConnected;
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _lastBroadcastTimes = new(StringComparer.Ordinal);
    private readonly List<string> _serials = [];
    private CancellationTokenSource? _stop;
    private Task? _pollTask;
    private string _status = "Checking USB connection…";
    private string? _adbPath;
    private bool _disposed;

    public event Action<AdbWiredSnapshot>? SnapshotChanged;

    public AdbWiredSnapshot Snapshot => GetSnapshot();

    public NativeAdbWiredService(
        string hostId,
        string hostName,
        int port = ProtocolV1.Port,
        Func<bool>? isConnected = null)
    {
        _hostId = hostId ?? throw new ArgumentNullException(nameof(hostId));
        _hostName = hostName ?? throw new ArgumentNullException(nameof(hostName));
        _port = port;
        _isConnected = isConnected ?? (() => false);
        _adbPath = FindAdbPath();
    }

    public AdbWiredSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new AdbWiredSnapshot(
                !string.IsNullOrEmpty(_adbPath),
                _adbPath,
                _serials.ToArray(),
                _status);
        }
    }

    public static string? FindAdbPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "adb.exe"),
            Path.Combine(AppContext.BaseDirectory, "scrcpy", "adb.exe"),
            @"C:\Users\Mehnaz\scrcpy\scrcpy-win64-v2.4\adb.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Android\Sdk\platform-tools\adb.exe"),
            @"F:\Software\scrcpy\adb.exe",
            @"C:\Program Files\scrcpy\adb.exe",
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var full = Path.Combine(dir.Trim(), "adb.exe");
                if (File.Exists(full)) return full;
            }
        }

        return null;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _pollTask is not null) return;
            _stop = new CancellationTokenSource();
            _pollTask = Task.Run(() => PollLoopAsync(_stop.Token));
        }
    }

    public async Task TriggerScanAsync(CancellationToken cancellationToken = default)
    {
        _adbPath = FindAdbPath();
        if (string.IsNullOrEmpty(_adbPath)) return;
        await PollOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                    break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lock (_sync) _status = $"USB monitor notice: {ex.Message}";
                NotifyChange();
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var adb = _adbPath ?? FindAdbPath();
        _adbPath = adb;

        if (string.IsNullOrEmpty(adb))
        {
            lock (_sync)
            {
                _serials.Clear();
                _status = "adb not found. Connect phone to Wi-Fi or place adb in app folder.";
            }
            NotifyChange();
            return;
        }

        var devicesOutput = await RunCommandAsync(adb, "devices", 4000, cancellationToken).ConfigureAwait(false);
        var activeSerials = ParseDeviceSerials(devicesOutput);

        lock (_sync)
        {
            _serials.Clear();
            _serials.AddRange(activeSerials);
        }

        if (activeSerials.Count == 0)
        {
            lock (_sync) _status = "No USB device detected. Plug in your Android phone via USB cable.";
            NotifyChange();
            return;
        }

        var connected = _isConnected();
        foreach (var serial in activeSerials)
        {
            await SetupReverseAsync(adb, serial, cancellationToken).ConfigureAwait(false);

            if (!connected)
            {
                var shouldAnnounce = false;
                lock (_sync)
                {
                    if (!_lastBroadcastTimes.TryGetValue(serial, out var last) ||
                        DateTimeOffset.UtcNow - last > TimeSpan.FromSeconds(15))
                    {
                        shouldAnnounce = true;
                        _lastBroadcastTimes[serial] = DateTimeOffset.UtcNow;
                    }
                }

                if (shouldAnnounce)
                {
                    await AnnounceToPhoneAsync(adb, serial, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        lock (_sync)
        {
            _status = connected
                ? $"USB session connected ({activeSerials.Count} device(s) active)"
                : $"USB device ready ({string.Join(", ", activeSerials)}). Routing port {_port}…";
        }
        NotifyChange();
    }

    private static List<string> ParseDeviceSerials(string output)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                continue;

            var match = Regex.Match(trimmed, @"^(\S+)\s+device$");
            if (match.Success)
            {
                var serial = match.Groups[1].Value;
                // Exclude network/TCP devices or offline devices
                if (!serial.Contains(':') && !serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(serial);
                }
            }
        }
        return result;
    }

    private async Task SetupReverseAsync(string adb, string serial, CancellationToken cancellationToken)
    {
        await RunCommandAsync(adb, $"-s {serial} reverse tcp:{_port} tcp:{_port}", 4000, cancellationToken).ConfigureAwait(false);
    }

    private async Task AnnounceToPhoneAsync(string adb, string serial, CancellationToken cancellationToken)
    {
        var pmCheck = await RunCommandAsync(adb, $"-s {serial} shell pm path {TargetAppId}", 4000, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(pmCheck) || !pmCheck.Contains("package:", StringComparison.OrdinalIgnoreCase))
            return;

        // Ensure app can respond in background
        await RunCommandAsync(adb, $"-s {serial} shell cmd deviceidle tempwhitelist -d 30000 {TargetAppId}", 4000, cancellationToken).ConfigureAwait(false);

        // Generate a random 8-byte hex nonce
        var nonceBytes = new byte[8];
        RandomNumberGenerator.Fill(nonceBytes);
        var nonce = Convert.ToHexString(nonceBytes).ToLowerInvariant();

        var escapedName = _hostName.Replace("'", @"\'");
        var broadcastCmd = $"-s {serial} shell am broadcast -f 32 -n {TargetAppId}/.WiredReceiver --es host {_hostId} --ei port {_port} --es nonce {nonce} --es name '{escapedName}'";
        await RunCommandAsync(adb, broadcastCmd, 5000, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> RunCommandAsync(
        string executable,
        string arguments,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            proc.Start();
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var readOutTask = proc.StandardOutput.ReadToEndAsync(linked.Token);
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            return await readOutTask.ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private void NotifyChange() => SnapshotChanged?.Invoke(GetSnapshot());

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }

        if (_stop is not null)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            if (_pollTask is not null)
            {
                try { await _pollTask.ConfigureAwait(false); } catch { }
            }
            _stop.Dispose();
        }
    }
}
