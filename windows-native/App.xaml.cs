using Microsoft.UI.Xaml;
using TwinDesk.Native.Core;
using TwinDesk.Native.Core.Discovery;

namespace TwinDesk.Native;

public partial class App : Application
{
    private Window? _window;
    private NativeCoreClient? _core;

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _core = new NativeCoreClient(new DiscoveryOptions());
        _window = new MainWindow(_core);
        _window.Activate();

        try
        {
            await _core.StartAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Native discovery could not start: {ex}");
        }
    }
}
