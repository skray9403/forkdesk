using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TwinDesk.Native.Core;

public sealed record EdgeCrossingOptions(
    string Side = "right",
    double ResistancePx = 50.0,
    bool Enabled = true,
    bool BleInput = false,
    bool ShakeHome = true);

public sealed record EdgeCrossingSnapshot(
    bool IsCrossing,
    string ActiveSide,
    double ResistancePx,
    bool Enabled,
    bool BleInput,
    int RemoteX,
    int RemoteY);

/// <summary>
/// Native service that allows the Windows mouse cursor to seamlessly push past the screen
/// boundary and carry on directly onto the connected Android phone screen (or computer peer).
/// Renders the real-time arrow overlay via Pointer.kt (phone.pointer) and injects touches/gestures
/// via phone.in and wired ADB fallback.
/// </summary>
public sealed class NativeEdgeCrossingService : IDisposable
{
    private readonly Func<string, CancellationToken, Task<bool>> _sendPeerMessage;
    private readonly NativeAdbWiredService? _adbService;
    private EdgeCrossingOptions _options = new();
    private EdgeCrossingSnapshot _snapshot;
    private readonly object _sync = new();

    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _mouseHook = IntPtr.Zero;
    private IntPtr _keyboardHook = IntPtr.Zero;
    private HookProc? _mouseProcDelegate;
    private HookProc? _keyboardProcDelegate;
    private readonly ManualResetEventSlim _readySignal = new(false);

    private volatile bool _isCrossing;
    private volatile bool _disposed;
    private int _targetWidth = 1080;
    private int _targetHeight = 2400;

    private double _phoneX;
    private double _phoneY;
    private int _parkedX;
    private int _parkedY;
    private double _pushOutward;
    private int _lastMouseX;
    private int _lastMouseY;
    private long _lastSendTicks;

    // Shake detection: track rapid direction reversals
    private readonly Queue<(long ticks, int dx)> _recentMoves = new();

    public event Action<EdgeCrossingSnapshot>? SnapshotChanged;

    public EdgeCrossingSnapshot Snapshot
    {
        get
        {
            lock (_sync) return _snapshot;
        }
    }

    public NativeEdgeCrossingService(
        Func<string, CancellationToken, Task<bool>> sendPeerMessage,
        NativeAdbWiredService? adbService = null)
    {
        _sendPeerMessage = sendPeerMessage ?? throw new ArgumentNullException(nameof(sendPeerMessage));
        _adbService = adbService;
        _snapshot = new EdgeCrossingSnapshot(false, _options.Side, _options.ResistancePx, _options.Enabled, _options.BleInput, 0, 0);

        if (OperatingSystem.IsWindows())
        {
            StartHookThread();
        }
    }

    public void SetOptions(EdgeCrossingOptions options)
    {
        lock (_sync)
        {
            _options = options;
            _snapshot = _snapshot with
            {
                ActiveSide = options.Side,
                ResistancePx = options.ResistancePx,
                Enabled = options.Enabled,
                BleInput = options.BleInput
            };
        }
        SnapshotChanged?.Invoke(Snapshot);
    }

    public void SetTargetResolution(int width, int height)
    {
        if (width > 0 && height > 0)
        {
            _targetWidth = width;
            _targetHeight = height;
        }
    }

    public void ReturnHome()
    {
        if (!_isCrossing) return;
        _isCrossing = false;
        _pushOutward = 0;

        _ = SendPointerOffAsync();

        // Restore Windows cursor slightly inward from the screen border
        GetScreenBounds(out var screenLeft, out var screenTop, out var screenWidth, out var screenHeight);
        var restoreX = _options.Side switch
        {
            "right" => screenLeft + screenWidth - 25,
            "left" => screenLeft + 25,
            _ => _parkedX
        };
        var restoreY = _options.Side switch
        {
            "bottom" => screenTop + screenHeight - 25,
            "top" => screenTop + 25,
            _ => (int)Math.Clamp(_phoneY / Math.Max(1, _targetHeight) * screenHeight + screenTop, screenTop + 10, screenTop + screenHeight - 10)
        };

        SetCursorPos(restoreX, restoreY);

        lock (_sync)
        {
            _snapshot = _snapshot with { IsCrossing = false };
        }
        SnapshotChanged?.Invoke(Snapshot);
    }

    private void StartHookThread()
    {
        _hookThread = new Thread(HookThreadWorker)
        {
            IsBackground = true,
            Name = "TwinDesk.EdgeCrossing.HookThread"
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        _readySignal.Wait(2000);
    }

    private void HookThreadWorker()
    {
        _hookThreadId = GetCurrentThreadId();
        _mouseProcDelegate = MouseHookCallback;
        _keyboardProcDelegate = KeyboardHookCallback;

        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var hMod = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;

        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProcDelegate, hMod, 0);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProcDelegate, hMod, 0);

        _readySignal.Set();

        while (!_disposed && GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !_options.Enabled)
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var m = ReadMouse(lParam);
        var msg = wParam.ToInt32();

        GetScreenBounds(out var screenLeft, out var screenTop, out var screenWidth, out var screenHeight);
        var screenRight = screenLeft + screenWidth;
        var screenBottom = screenTop + screenHeight;

        if (!_isCrossing)
        {
            if (msg == WM_MOUSEMOVE)
            {
                var atEdge = false;
                var outward = 0.0;

                switch (_options.Side.ToLowerInvariant())
                {
                    case "right":
                        if (m.pt.x >= screenRight - 2)
                        {
                            atEdge = true;
                            outward = Math.Max(2.0, m.pt.x - _lastMouseX + 3.0);
                        }
                        break;
                    case "left":
                        if (m.pt.x <= screenLeft + 1)
                        {
                            atEdge = true;
                            outward = Math.Max(2.0, _lastMouseX - m.pt.x + 3.0);
                        }
                        break;
                    case "top":
                        if (m.pt.y <= screenTop + 1)
                        {
                            atEdge = true;
                            outward = Math.Max(2.0, _lastMouseY - m.pt.y + 3.0);
                        }
                        break;
                    case "bottom":
                        if (m.pt.y >= screenBottom - 2)
                        {
                            atEdge = true;
                            outward = Math.Max(2.0, m.pt.y - _lastMouseY + 3.0);
                        }
                        break;
                }

                _lastMouseX = m.pt.x;
                _lastMouseY = m.pt.y;

                if (atEdge)
                {
                    _pushOutward += outward;
                    if (_pushOutward >= _options.ResistancePx)
                    {
                        // Cross onto phone!
                        _isCrossing = true;
                        _pushOutward = 0;
                        _parkedX = m.pt.x;
                        _parkedY = m.pt.y;

                        switch (_options.Side.ToLowerInvariant())
                        {
                            case "right":
                                _phoneX = 2;
                                _phoneY = (int)Math.Clamp((m.pt.y - screenTop) / (double)Math.Max(1, screenHeight) * _targetHeight, 0, _targetHeight - 1);
                                break;
                            case "left":
                                _phoneX = _targetWidth - 3;
                                _phoneY = (int)Math.Clamp((m.pt.y - screenTop) / (double)Math.Max(1, screenHeight) * _targetHeight, 0, _targetHeight - 1);
                                break;
                            case "top":
                                _phoneX = (int)Math.Clamp((m.pt.x - screenLeft) / (double)Math.Max(1, screenWidth) * _targetWidth, 0, _targetWidth - 1);
                                _phoneY = _targetHeight - 3;
                                break;
                            case "bottom":
                                _phoneX = (int)Math.Clamp((m.pt.x - screenLeft) / (double)Math.Max(1, screenWidth) * _targetWidth, 0, _targetWidth - 1);
                                _phoneY = 2;
                                break;
                        }

                        _ = SendPointerPositionAsync((int)_phoneX, (int)_phoneY);

                        lock (_sync)
                        {
                            _snapshot = _snapshot with
                            {
                                IsCrossing = true,
                                RemoteX = (int)_phoneX,
                                RemoteY = (int)_phoneY
                            };
                        }
                        SnapshotChanged?.Invoke(Snapshot);

                        SetCursorPos(_parkedX, _parkedY);
                        return (IntPtr)1;
                    }
                }
                else
                {
                    _pushOutward = 0;
                }
            }

            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // WHILE CROSSING IS ACTIVE:
        if (msg == WM_MOUSEMOVE)
        {
            var dx = m.pt.x - _parkedX;
            var dy = m.pt.y - _parkedY;

            if (dx == 0 && dy == 0)
                return (IntPtr)1;

            // Keep cursor parked so Windows desktop cursor doesn't drift
            SetCursorPos(_parkedX, _parkedY);

            // Check shake-to-return
            if (_options.ShakeHome && CheckShakeReturn(dx))
            {
                ReturnHome();
                return (IntPtr)1;
            }

            const double speedMultiplier = 1.35;
            _phoneX += dx * speedMultiplier;
            _phoneY += dy * speedMultiplier;

            // Check if pulling back into Windows
            var returning = _options.Side.ToLowerInvariant() switch
            {
                "right" => _phoneX < -_options.ResistancePx,
                "left" => _phoneX > _targetWidth + _options.ResistancePx,
                "top" => _phoneY > _targetHeight + _options.ResistancePx,
                "bottom" => _phoneY < -_options.ResistancePx,
                _ => false
            };

            if (returning)
            {
                ReturnHome();
                return (IntPtr)1;
            }

            var cx = (int)Math.Clamp(_phoneX, 0, _targetWidth - 1);
            var cy = (int)Math.Clamp(_phoneY, 0, _targetHeight - 1);

            // Rate-limit pointer updates to ~12ms max frequency
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastSendTicks, now).TotalMilliseconds >= 12.0)
            {
                _lastSendTicks = now;
                _ = SendPointerPositionAsync(cx, cy);

                lock (_sync)
                {
                    _snapshot = _snapshot with { RemoteX = cx, RemoteY = cy };
                }
                SnapshotChanged?.Invoke(Snapshot);
            }

            return (IntPtr)1;
        }

        var px = (int)Math.Clamp(_phoneX, 0, _targetWidth - 1);
        var py = (int)Math.Clamp(_phoneY, 0, _targetHeight - 1);

        if (msg == WM_LBUTTONDOWN)
        {
            _ = SendPhoneInAsync("d", px, py);
            return (IntPtr)1;
        }

        if (msg == WM_LBUTTONUP)
        {
            _ = SendPhoneInAsync("u", px, py);
            // Also trigger ADB tap fallback for 100% responsiveness
            _ = TriggerAdbTapAsync(px, py);
            return (IntPtr)1;
        }

        if (msg == WM_RBUTTONDOWN)
        {
            // Right-click = Android Back
            _ = SendPhoneKeyAsync(down: true, keycode: 4); // KEYCODE_BACK
            return (IntPtr)1;
        }

        if (msg == WM_RBUTTONUP)
        {
            _ = SendPhoneKeyAsync(down: false, keycode: 4);
            return (IntPtr)1;
        }

        if (msg is WM_MOUSEWHEEL or WM_MOUSEHWHEEL)
        {
            var notches = (short)((m.mouseData >> 16) & 0xFFFF) / 120.0;
            var h = msg == WM_MOUSEHWHEEL ? notches : 0.0;
            var v = msg == WM_MOUSEWHEEL ? -notches : 0.0;
            _ = SendPhoneScrollAsync(px, py, h, v);
            return (IntPtr)1;
        }

        return (IntPtr)1;
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !_isCrossing)
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var k = ReadKey(lParam);
        var msg = wParam.ToInt32();
        var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;

        // Emergency escape keys: ESC or Right-Alt returns home immediately
        if (k.vkCode is 0x1B or 0xA5)
        {
            if (down) ReturnHome();
            return (IntPtr)1;
        }

        // Map keycode and forward to phone
        var androidCode = MapWindowsVkToAndroid(k.vkCode);
        if (androidCode > 0)
        {
            _ = SendPhoneKeyAsync(down, androidCode);
            if (down && _adbService is not null)
            {
                _ = TriggerAdbKeyAsync(androidCode);
            }
        }

        return (IntPtr)1;
    }

    private bool CheckShakeReturn(int dx)
    {
        if (Math.Abs(dx) < 18) return false;

        var now = Stopwatch.GetTimestamp();
        _recentMoves.Enqueue((now, dx));

        // Purge moves older than 400ms
        while (_recentMoves.Count > 0 && Stopwatch.GetElapsedTime(_recentMoves.Peek().ticks, now).TotalMilliseconds > 400.0)
        {
            _recentMoves.Dequeue();
        }

        if (_recentMoves.Count < 4) return false;

        var reversals = 0;
        var lastDir = 0;
        foreach (var (_, move) in _recentMoves)
        {
            var dir = Math.Sign(move);
            if (dir != 0 && lastDir != 0 && dir != lastDir)
                reversals++;
            lastDir = dir;
        }

        if (reversals >= 3)
        {
            _recentMoves.Clear();
            return true;
        }

        return false;
    }

    private static int MapWindowsVkToAndroid(uint vk) => vk switch
    {
        0x0D => 66, // VK_RETURN -> KEYCODE_ENTER
        0x08 => 67, // VK_BACK -> KEYCODE_DEL
        0x09 => 61, // VK_TAB -> KEYCODE_TAB
        0x20 => 62, // VK_SPACE -> KEYCODE_SPACE
        0x25 => 21, // VK_LEFT -> KEYCODE_DPAD_LEFT
        0x26 => 19, // VK_UP -> KEYCODE_DPAD_UP
        0x27 => 22, // VK_RIGHT -> KEYCODE_DPAD_RIGHT
        0x28 => 20, // VK_DOWN -> KEYCODE_DPAD_DOWN
        0x24 => 3,  // VK_HOME -> KEYCODE_HOME
        0x23 => 123,// VK_END -> KEYCODE_MOVE_END
        0x2E => 112,// VK_DELETE -> KEYCODE_FORWARD_DEL
        >= 0x30 and <= 0x39 => (int)(vk - 0x30 + 7), // 0-9 -> KEYCODE_0-9
        >= 0x41 and <= 0x5A => (int)(vk - 0x41 + 29), // A-Z -> KEYCODE_A-Z
        _ => 0
    };

    private async Task SendPointerPositionAsync(int x, int y)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                t = "phone.pointer",
                on = true,
                x,
                y
            });
            await _sendPeerMessage(json, CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task SendPointerOffAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                t = "phone.pointer",
                on = false
            });
            await _sendPeerMessage(json, CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task SendPhoneInAsync(string action, int x, int y)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                t = "phone.in",
                a = action,
                x,
                y
            });
            await _sendPeerMessage(json, CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task SendPhoneKeyAsync(bool down, int keycode)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                t = "phone.in",
                a = "k",
                d = down ? 1 : 0,
                k = keycode,
                m = 0
            });
            await _sendPeerMessage(json, CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task SendPhoneScrollAsync(int x, int y, double h, double v)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                t = "phone.in",
                a = "s",
                x,
                y,
                h,
                v
            });
            await _sendPeerMessage(json, CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task TriggerAdbTapAsync(int x, int y)
    {
        if (_adbService is null) return;
        var adb = NativeAdbWiredService.FindAdbPath();
        if (adb is null) return;
        var snapshot = _adbService.Snapshot;
        if (snapshot.ConnectedSerials.Count == 0) return;
        var serial = snapshot.ConnectedSerials[0];

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = adb,
                Arguments = $"-s {serial} shell input tap {x} {y}",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc != null) await proc.WaitForExitAsync().ConfigureAwait(false);
        }
        catch { }
    }

    private async Task TriggerAdbKeyAsync(int keycode)
    {
        if (_adbService is null) return;
        var adb = NativeAdbWiredService.FindAdbPath();
        if (adb is null) return;
        var snapshot = _adbService.Snapshot;
        if (snapshot.ConnectedSerials.Count == 0) return;
        var serial = snapshot.ConnectedSerials[0];

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = adb,
                Arguments = $"-s {serial} shell input keyevent {keycode}",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc != null) await proc.WaitForExitAsync().ConfigureAwait(false);
        }
        catch { }
    }

    private static void GetScreenBounds(out int left, out int top, out int width, out int height)
    {
        left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (width <= 0 || height <= 0)
        {
            left = 0;
            top = 0;
            width = GetSystemMetrics(SM_CXSCREEN);
            height = GetSystemMetrics(SM_CYSCREEN);
            if (width <= 0) width = 1920;
            if (height <= 0) height = 1080;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReturnHome();

        if (_hookThreadId != 0)
        {
            PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _hookThread?.Join(1000);
        }

        _readySignal.Dispose();
    }

    #region Win32 P/Invoke
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_MOUSEHWHEEL = 0x020E;
    private const uint WM_QUIT = 0x0012;

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private static MSLLHOOKSTRUCT ReadMouse(IntPtr p) =>
        Marshal.PtrToStructure<MSLLHOOKSTRUCT>(p);

    private static KBDLLHOOKSTRUCT ReadKey(IntPtr p) =>
        Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(p);
    #endregion
}
