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
    private string _activeSide = "right";
    private string _peerName = "the other device";

    public RemotePage()
    {
        InitializeComponent();
        LayoutPcNameText.Text = Environment.MachineName;
    }

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
        if (snapshot.ConnectedDevice is not null)
        {
            _peerName = snapshot.ConnectedDevice.Name;
        }

        UpdateLayoutHint();

        var remote = snapshot.RemoteInput;
        if (remote is not null)
        {
            StatusText.Text = remote.Enabled
                ? (snapshot.ConnectedDevice is not null
                    ? $"Connected to {_peerName}. Touchpad, keyboard, and cross-screen edge tracking active."
                    : "Remote input enabled. Connect an Android phone or computer to begin.")
                : "Remote input is currently disabled.";

            LastEventText.Text = $"Last input action: {remote.LastEvent}";
        }
        else
        {
            StatusText.Text = "Input engine ready.";
        }
    }

    private void SlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string side }) return;
        _activeSide = side;

        // Reset all slot button styles
        SlotTopButton.Style = null;
        SlotBottomButton.Style = null;
        SlotLeftButton.Style = null;
        SlotRightButton.Style = null;

        var accentStyle = (Style)Application.Current.Resources["AccentButtonStyle"];

        switch (side)
        {
            case "top":
                SlotTopButton.Style = accentStyle;
                SelectedSidePillText.Text = "Above";
                break;
            case "bottom":
                SlotBottomButton.Style = accentStyle;
                SelectedSidePillText.Text = "Below";
                break;
            case "left":
                SlotLeftButton.Style = accentStyle;
                SelectedSidePillText.Text = "Left side";
                break;
            case "right":
            default:
                SlotRightButton.Style = accentStyle;
                SelectedSidePillText.Text = "Right side";
                break;
        }

        UpdateLayoutHint();
        OnOptionChanged(sender, e);
    }

    private void UpdateLayoutHint()
    {
        var sideDesc = _activeSide switch
        {
            "top" => "top edge",
            "bottom" => "bottom edge",
            "left" => "left edge",
            _ => "right edge"
        };

        LayoutCrossingHintText.Text = $"Push the mouse pointer past the {sideDesc} of your screen to cross over to {_peerName}.";
    }

    private void ResistanceSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ResistanceValueText is not null)
            ResistanceValueText.Text = e.NewValue > 0 ? $"{e.NewValue:0} px" : "None";
    }

    private void BleInputToggle_Toggled(object sender, RoutedEventArgs e)
    {
        OnOptionChanged(sender, e);
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
