using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TwinDesk.Native.Core;

namespace TwinDesk.Native.Pages;

public sealed partial class AudioPage : Page
{
    private INativeCoreClient? _core;
    private DispatcherQueue? _dispatcherQueue;

    public AudioPage() => InitializeComponent();

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
        var audio = snapshot.AudioRouting;
        if (audio is not null)
        {
            StatusText.Text = audio.PlayPhoneSound
                ? (snapshot.ConnectedDevice is not null
                    ? $"Playing sound from {snapshot.ConnectedDevice.Name} ({audio.Status})"
                    : "Phone sound playback requested. Waiting for phone connection.")
                : "Audio playback from phone is turned off.";
        }
        else
        {
            StatusText.Text = "Audio receiver ready.";
        }
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (VolumeText is not null)
            VolumeText.Text = $"{(int)e.NewValue}%";
        OnOptionChanged(sender, new RoutedEventArgs());
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_core is null) return;

        var options = new AudioRoutingOptions(
            PlayPhoneSound: PlayPhoneAudioToggle.IsOn,
            VolumePercent: (int)VolumeSlider.Value,
            ForwardMicrophone: false);

        _ = _core.SetAudioRoutingOptionsAsync(options);
    }
}
