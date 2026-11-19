using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TwinDesk.Native.Core;

namespace TwinDesk.Native.Pages;

public sealed partial class RemotePage : Page
{
    private INativeCoreClient? _core;
    private DispatcherQueue? _dispatcherQueue;

    public RemotePage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not INativeCoreClient core)
        {
            StatusText.Text = "Native core service is not connected to this page.";
            return;
        }

        if (_core is not null)
            _core.SnapshotChanged -= OnSnapshotChanged;

        _core = core;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _core.SnapshotChanged += OnSnapshotChanged;
        _ = LoadSnapshotAsync(core);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_core is not null)
            _core.SnapshotChanged -= OnSnapshotChanged;

        _core = null;
        base.OnNavigatedFrom(e);
    }

    private async Task LoadSnapshotAsync(INativeCoreClient core)
    {
        try
        {
            var snapshot = await core.GetSnapshotAsync();
            _dispatcherQueue?.TryEnqueue(() => ApplySnapshot(snapshot));
        }
        catch (Exception ex)
        {
            _dispatcherQueue?.TryEnqueue(() => StatusText.Text = $"Error reading status: {ex.Message}");
        }
    }

    private void OnSnapshotChanged(object? sender, NativeCoreSnapshot snapshot) =>
        _dispatcherQueue?.TryEnqueue(() => ApplySnapshot(snapshot));

    private void ApplySnapshot(NativeCoreSnapshot snapshot)
    {
        var remote = snapshot.RemoteInput;
        if (remote is not null)
        {
            StatusText.Text = remote.Enabled
                ? (snapshot.ConnectedDevice is not null
                    ? $"Connected to {snapshot.ConnectedDevice.Name}. Phone touchpad and keyboard active."
                    : "Remote input enabled. Connect an Android phone to control this PC.")
                : "Remote input is currently disabled.";

            LastEventText.Text = $"Last input action: {remote.LastEvent}";
        }
        else
        {
            StatusText.Text = "Ready to receive input.";
        }
    }

    private void SpeedSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (SpeedText is not null)
            SpeedText.Text = $"{e.NewValue:0.1}x";
        OnOptionChanged(sender, new RoutedEventArgs());
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_core is null) return;

        var options = new RemoteInputOptions(
            Enabled: EnabledToggle.IsOn,
            Speed: SpeedSlider.Value,
            Smooth: SmoothCheckBox.IsChecked == true,
            TapClick: TapClickCheckBox.IsChecked == true,
            AllowKeyboard: KeyboardCheckBox.IsChecked == true);

        _ = _core.SetRemoteInputOptionsAsync(options);
    }
}
