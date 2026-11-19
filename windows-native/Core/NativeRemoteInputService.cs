using System.Runtime.InteropServices;
using System.Text.Json;

namespace TwinDesk.Native.Core;

public sealed record RemoteInputOptions(
    bool Enabled = true,
    double Speed = 1.0,
    bool Smooth = true,
    bool TapClick = true,
    bool AllowKeyboard = true);

public sealed record RemoteInputSnapshot(
    bool Enabled,
    double Speed,
    bool Smooth,
    bool TapClick,
    bool AllowKeyboard,
    string LastEvent);

public sealed class NativeRemoteInputService
{
    private readonly Func<string, CancellationToken, Task<bool>> _sendPeerMessage;
    private RemoteInputOptions _options = new();
    private string _lastEvent = "Idle";
    private readonly object _sync = new();

    public event Action<RemoteInputSnapshot>? SnapshotChanged;

    public NativeRemoteInputService(Func<string, CancellationToken, Task<bool>> sendPeerMessage)
    {
        _sendPeerMessage = sendPeerMessage ?? throw new ArgumentNullException(nameof(sendPeerMessage));
    }

    public RemoteInputSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return new RemoteInputSnapshot(
                    _options.Enabled,
                    _options.Speed,
                    _options.Smooth,
                    _options.TapClick,
                    _options.AllowKeyboard,
                    _lastEvent);
            }
        }
    }

    public void SetOptions(RemoteInputOptions options)
    {
        lock (_sync)
        {
            _options = options;
        }
        SnapshotChanged?.Invoke(Snapshot);
    }

    public async Task HandleSessionStartedAsync(CancellationToken cancellationToken = default)
    {
        var stateMsg = JsonSerializer.Serialize(new
        {
            t = "remote.state",
            allowed = _options.Enabled,
            why = _options.Enabled ? "" : "Remote input disabled in TwinDesk Windows settings",
            voice = _options.AllowKeyboard,
            voiceWhy = _options.AllowKeyboard ? "" : "Keyboard input disabled in TwinDesk Windows settings",
            pad = false
        });
        await _sendPeerMessage(stateMsg, cancellationToken).ConfigureAwait(false);
    }

    public async Task HandlePeerMessageAsync(string type, JsonElement message, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled && type.StartsWith("remote.", StringComparison.Ordinal))
            return;

        switch (type)
        {
            case "remote.hello":
                await HandleSessionStartedAsync(cancellationToken).ConfigureAwait(false);
                break;

            case "remote.mm":
                if (message.TryGetProperty("dx", out var dxEl) && message.TryGetProperty("dy", out var dyEl))
                {
                    var dx = (int)Math.Round(dxEl.GetDouble() * _options.Speed);
                    var dy = (int)Math.Round(dyEl.GetDouble() * _options.Speed);
                    SendMouseMove(dx, dy);
                    UpdateEvent($"Mouse move ({dx}, {dy})");
                }
                break;

            case "remote.md":
                if (message.TryGetProperty("b", out var btnDownEl))
                {
                    var btn = btnDownEl.GetInt32();
                    SendMouseButton(btn, down: true);
                    UpdateEvent($"Mouse button {btn} down");
                }
                break;

            case "remote.mu":
                if (message.TryGetProperty("b", out var btnUpEl))
                {
                    var btn = btnUpEl.GetInt32();
                    SendMouseButton(btn, down: false);
                    UpdateEvent($"Mouse button {btn} up");
                }
                break;

            case "remote.wh":
                if (message.TryGetProperty("dy", out var whYEl))
                {
                    var dy = (int)Math.Round(whYEl.GetDouble() * 120);
                    SendMouseWheel(dy);
                    UpdateEvent($"Mouse scroll {dy}");
                }
                break;

            case "remote.text":
                if (_options.AllowKeyboard && message.TryGetProperty("text", out var textEl))
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        SendText(text);
                        UpdateEvent($"Typed {text.Length} chars");
                    }
                }
                break;
        }
    }

    private void UpdateEvent(string desc)
    {
        lock (_sync)
        {
            _lastEvent = desc;
        }
        SnapshotChanged?.Invoke(Snapshot);
    }

    #region Win32 SendInput P/Invoke

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    private static void SendMouseMove(int dx, int dy)
    {
        if (!OperatingSystem.IsWindows()) return;
        var inputs = new INPUT[]
        {
            new()
            {
                type = INPUT_MOUSE,
                u = new INPUTUNION
                {
                    mi = new MOUSEINPUT
                    {
                        dx = dx,
                        dy = dy,
                        dwFlags = MOUSEEVENTF_MOVE
                    }
                }
            }
        };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseButton(int button, bool down)
    {
        if (!OperatingSystem.IsWindows()) return;
        uint flag = button switch
        {
            0 => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            1 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            2 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            _ => 0
        };
        if (flag == 0) return;

        var inputs = new INPUT[]
        {
            new()
            {
                type = INPUT_MOUSE,
                u = new INPUTUNION
                {
                    mi = new MOUSEINPUT { dwFlags = flag }
                }
            }
        };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseWheel(int delta)
    {
        if (!OperatingSystem.IsWindows()) return;
        var inputs = new INPUT[]
        {
            new()
            {
                type = INPUT_MOUSE,
                u = new INPUTUNION
                {
                    mi = new MOUSEINPUT
                    {
                        mouseData = unchecked((uint)delta),
                        dwFlags = MOUSEEVENTF_WHEEL
                    }
                }
            }
        };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendText(string text)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(text)) return;
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wScan = ch,
                        dwFlags = KEYEVENTF_UNICODE
                    }
                }
            });
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wScan = ch,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
                    }
                }
            });
        }
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    #endregion
}
