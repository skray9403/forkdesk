using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TwinDesk.Native.Core;

namespace TwinDesk.Native.Pages;

public sealed partial class ScreenPage : Page
{
    private INativeCoreClient? _core;
    private DispatcherQueue? _dispatcherQueue;

    public ScreenPage() => InitializeComponent();

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
        var mirror = snapshot.ScreenMirror;
        if (mirror is not null)
        {
            StatusText.Text = mirror.IsRunning
                ? "Screen mirroring active"
                : mirror.Status;

            StartMirrorButton.IsEnabled = !mirror.IsRunning;
            StopMirrorButton.IsEnabled = mirror.IsRunning;
            EnginePathText.Text = mirror.ScrcpyPath is not null
                ? $"Mirror engine: {mirror.ScrcpyPath}"
                : "scrcpy engine not detected. Install scrcpy or ensure it is in PATH.";
        }
        else
        {
            StatusText.Text = snapshot.ConnectedDevice is not null
                ? $"Connected to {snapshot.ConnectedDevice.Name}. Ready to mirror."
                : "No phone connected. Connect an Android device to start mirroring.";
        }
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_core is null) return;

        int maxSize = ResolutionCombo.SelectedIndex switch
        {
            1 => 2560,
            2 => 1280,
            3 => 0,
            _ => 1920
        };

        int maxFps = FpsCombo.SelectedIndex == 1 ? 30 : 60;
        int bitrate = BitrateCombo.SelectedIndex switch
        {
            1 => 4,
            2 => 2,
            _ => 8
        };

        var options = new ScreenMirrorOptions(
            MaxSize: maxSize,
            MaxFps: maxFps,
            BitRateMbps: bitrate,
            StayAwake: StayAwakeCheckBox.IsChecked == true,
            TurnScreenOff: TurnScreenOffCheckBox.IsChecked == true,
            Audio: AudioCheckBox.IsChecked == true);

        _core.SetScreenMirrorOptions(options);
    }

    private void StartMirror_Click(object sender, RoutedEventArgs e)
    {
        if (_core is null) return;
        OnOptionChanged(sender, e);
        _core.StartScreenMirroring();
    }

    private void StopMirror_Click(object sender, RoutedEventArgs e)
    {
        _core?.StopScreenMirroring();
    }

    private void LaunchApp_Click(object sender, RoutedEventArgs e)
    {
        if (_core is null || sender is not Button { Tag: string tag }) return;
        var parts = tag.Split('|');
        var pkg = parts[0];
        var title = parts.Length > 1 ? parts[1] : pkg;

        AppLaunchStatusText.Text = $"Launching {title} in standalone window...";
        var success = _core.StartSingleApp(pkg, title);
        AppLaunchStatusText.Text = success
            ? $"Launched {title} ({pkg}) in standalone desktop window."
            : $"Failed to launch {title}. Ensure phone is unlocked and scrcpy is installed.";
    }

    private void LaunchCustomPackage_Click(object sender, RoutedEventArgs e)
    {
        if (_core is null) return;
        var pkg = CustomPackageBox.Text.Trim();
        if (string.IsNullOrEmpty(pkg))
        {
            AppLaunchStatusText.Text = "Please enter an Android package name (e.g. com.whatsapp).";
            return;
        }

        AppLaunchStatusText.Text = $"Launching {pkg}...";
        var success = _core.StartSingleApp(pkg, pkg);
        AppLaunchStatusText.Text = success
            ? $"Launched {pkg} in standalone desktop window."
            : $"Failed to launch {pkg}. Ensure package is installed and phone is connected.";
    }

    private async void InstallApk_Click(object sender, RoutedEventArgs e)
    {
        if (_core is null) return;
        var path = ApkPathBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            ApkStatusText.Text = "Please enter the full path to an .apk file.";
            return;
        }

        ApkStatusText.Text = $"Installing {System.IO.Path.GetFileName(path)} on phone...";
        var (success, msg) = await _core.InstallApkAsync(path);
        ApkStatusText.Text = msg;
    }
}
