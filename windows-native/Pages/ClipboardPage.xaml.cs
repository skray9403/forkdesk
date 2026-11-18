using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TwinDesk.Native.Core;

namespace TwinDesk.Native.Pages;

public sealed partial class ClipboardPage : Page
{
    private INativeCoreClient? _core;
    private DispatcherQueue? _dispatcherQueue;
    private bool _updating;

    public ClipboardPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_core is not null) _core.SnapshotChanged -= OnSnapshotChanged;
        if (e.Parameter is not INativeCoreClient core)
        {
            StatusText.Text = "Native clipboard services are not connected to this page.";
            return;
        }

        _core = core;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _core.SnapshotChanged += OnSnapshotChanged;
        _ = LoadSnapshotAsync(core);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_core is not null) _core.SnapshotChanged -= OnSnapshotChanged;
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
            _dispatcherQueue?.TryEnqueue(() => StatusText.Text = $"Unable to load clipboard state: {ex.Message}");
        }
    }

    private void OnSnapshotChanged(object? sender, NativeCoreSnapshot snapshot) =>
        _dispatcherQueue?.TryEnqueue(() => ApplySnapshot(snapshot));

    private void ApplySnapshot(NativeCoreSnapshot snapshot)
    {
        _updating = true;
        try
        {
            var clipboard = snapshot.Clipboard;
            if (clipboard is null)
            {
                EnabledCheckBox.IsEnabled = SendCheckBox.IsEnabled = ReceiveCheckBox.IsEnabled = false;
                StatusText.Text = "Windows clipboard access is unavailable.";
                ConnectionText.Text = snapshot.HostStatus;
                LastActivityText.Text = string.Empty;
                return;
            }

            EnabledCheckBox.IsEnabled = true;
            EnabledCheckBox.IsChecked = clipboard.Options.Enabled;
            SendCheckBox.IsEnabled = ReceiveCheckBox.IsEnabled = clipboard.Options.Enabled;
            SendCheckBox.IsChecked = clipboard.Options.Send;
            ReceiveCheckBox.IsChecked = clipboard.Options.Receive;
            StatusText.Text = clipboard.Status;
            ConnectionText.Text = clipboard.IsConnected
                ? clipboard.PeerAccepts ? "Connected peer accepts clipboard text." : "Connected peer is not accepting clipboard text."
                : "No authenticated peer is connected.";

            var activity = new List<string>();
            if (clipboard.LastSentAt is { } sent) activity.Add($"Last sent: {sent.ToLocalTime():t}");
            if (clipboard.LastReceivedAt is { } received) activity.Add($"Last received: {received.ToLocalTime():t}");
            LastActivityText.Text = activity.Count == 0
                ? "No clipboard text transferred in this session."
                : string.Join("   •   ", activity);
        }
        finally { _updating = false; }
    }

    private async void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        var core = _core;
        if (_updating || core is null) return;

        var options = new ClipboardSyncOptions(
            EnabledCheckBox.IsChecked == true,
            SendCheckBox.IsChecked == true,
            ReceiveCheckBox.IsChecked == true);
        try
        {
            await core.SetClipboardOptionsAsync(options);
            ApplySnapshot(await core.GetSnapshotAsync());
        }
        catch (Exception ex) { StatusText.Text = $"Could not update clipboard settings: {ex.Message}"; }
    }
}