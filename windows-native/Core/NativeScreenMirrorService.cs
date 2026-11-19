using System.Diagnostics;

namespace TwinDesk.Native.Core;

public sealed record ScreenMirrorOptions(
    int MaxSize = 1920,
    int MaxFps = 60,
    int BitRateMbps = 8,
    bool StayAwake = true,
    bool TurnScreenOff = false,
    bool Audio = false);

public sealed record ScreenMirrorSnapshot(
    bool IsRunning,
    string? ScrcpyPath,
    string Status,
    ScreenMirrorOptions Options);

public sealed class NativeScreenMirrorService : IDisposable
{
    private Process? _process;
    private ScreenMirrorOptions _options = new();
    private string _status = "Ready";
    private readonly object _sync = new();

    public event Action<ScreenMirrorSnapshot>? SnapshotChanged;

    public ScreenMirrorSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return new ScreenMirrorSnapshot(
                    _process is not null && !_process.HasExited,
                    FindScrcpyPath(),
                    _status,
                    _options);
            }
        }
    }

    public void SetOptions(ScreenMirrorOptions options)
    {
        lock (_sync)
        {
            _options = options;
        }
        SnapshotChanged?.Invoke(Snapshot);
    }

    public static string? FindScrcpyPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "scrcpy", "scrcpy.exe"),
            @"C:\Users\Mehnaz\scrcpy\scrcpy-win64-v2.4\scrcpy.exe",
            @"F:\Software\scrcpy\scrcpy.exe",
            @"C:\Program Files\scrcpy\scrcpy.exe",
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                var p = Path.Combine(dir.Trim(), "scrcpy.exe");
                if (File.Exists(p)) return p;
            }
        }

        return null;
    }

    public bool StartMirroring(string? deviceAddress = null)
    {
        lock (_sync)
        {
            if (_process is not null && !_process.HasExited)
                return true;

            var scrcpy = FindScrcpyPath();
            if (scrcpy is null)
            {
                _status = "scrcpy executable not found. Place scrcpy in the app folder or install scrcpy.";
                SnapshotChanged?.Invoke(Snapshot);
                return false;
            }

            var args = new List<string>
            {
                $"--max-size={_options.MaxSize}",
                $"--max-fps={_options.MaxFps}",
                $"--video-bit-rate={_options.BitRateMbps}M",
                "--window-title=\"TwinDesk Screen Mirror\""
            };

            if (_options.StayAwake) args.Add("--stay-awake");
            if (_options.TurnScreenOff) args.Add("--turn-screen-off");
            if (!_options.Audio) args.Add("--no-audio");

            if (!string.IsNullOrWhiteSpace(deviceAddress))
            {
                args.Add($"--tcpip={deviceAddress}:5555");
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = scrcpy,
                    Arguments = string.Join(" ", args),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                _process = Process.Start(psi);
                if (_process is not null)
                {
                    _process.EnableRaisingEvents = true;
                    _process.Exited += (_, _) =>
                    {
                        lock (_sync)
                        {
                            _status = "Mirroring session ended";
                        }
                        SnapshotChanged?.Invoke(Snapshot);
                    };
                    _status = "Screen mirroring active";
                    SnapshotChanged?.Invoke(Snapshot);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _status = $"Failed to start scrcpy: {ex.Message}";
                SnapshotChanged?.Invoke(Snapshot);
                return false;
            }

            return false;
        }
    }

    public void StopMirroring()
    {
        lock (_sync)
        {
            if (_process is not null && !_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); } catch { }
            }
            _process = null;
            _status = "Stopped";
            SnapshotChanged?.Invoke(Snapshot);
        }
    }

    public bool StartApp(string packageName, string title, string? serial = null)
    {
        var scrcpy = FindScrcpyPath();
        if (scrcpy is null) return false;

        var adb = NativeAdbWiredService.FindAdbPath();
        if (adb is not null)
        {
            try
            {
                var sArg = !string.IsNullOrEmpty(serial) ? $"-s {serial} " : "";
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = adb,
                    Arguments = $"{sArg}shell monkey -p {packageName} -c android.intent.category.LAUNCHER 1",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                p?.WaitForExit(3000);
            }
            catch { }
        }

        var args = new List<string>
        {
            $"--window-title=\"TwinDesk - {title}\""
        };
        if (!string.IsNullOrEmpty(serial))
        {
            args.Add($"-s {serial}");
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = scrcpy,
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()

    {
        StopMirroring();
    }
}
