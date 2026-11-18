using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using TwinDesk.Native.Core;
using TwinDesk.Native.Core.Discovery;
using TwinDesk.Native.Services;

namespace TwinDesk.Native;

public partial class App : Application
{
    private Window? _window;
    private NativeCoreClient? _core;
    private NativeIdentity? _identity;

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TwinDesk.Native");
            var identity = new NativeIdentityStore(dataDirectory).LoadOrCreate();
            _identity = identity;
            _core = new NativeCoreClient(identity, dataDirectory, new DiscoveryOptions
            {
                DeviceId = identity.Id,
                DeviceName = identity.Name,
                Fingerprint = identity.Fingerprint
            }, new WindowsClipboardAdapter());

            _window = new MainWindow(_core);
            _window.Closed += async (_, _) => await ShutdownAsync();
            _window.Activate();

            await _core.StartAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Native startup failed: {ex}");
            ShowStartupError(ex.Message);
        }
    }

    private async Task ShutdownAsync()
    {
        if (_core is not null)
            await _core.DisposeAsync();

        _identity?.Dispose();
        _identity = null;
    }

    private static void ShowStartupError(string message) =>
        MessageBoxW(IntPtr.Zero,
            "TwinDesk could not initialize its native device identity or host services." +
            Environment.NewLine + Environment.NewLine + message,
            "TwinDesk", 0x10);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
