using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TwinDesk.Native.Core;

namespace TwinDesk.Native.Pages;

public sealed partial class AboutPage : Page
{
    private INativeCoreClient? _core;
    private DispatcherQueue? _dispatcherQueue;

    public AboutPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_core is not null) _core.SnapshotChanged -= OnSnapshotChanged;

        _core = e.Parameter as INativeCoreClient;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        if (_core is not null)
        {
            _core.SnapshotChanged += OnSnapshotChanged;
            _ = LoadSnapshotAsync(_core);
        }

        PopulateSystemInfo();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_core is not null) _core.SnapshotChanged -= OnSnapshotChanged;
        _core = null;
        base.OnNavigatedFrom(e);
    }

    private void PopulateSystemInfo()
    {
        ArchitectureText.Text = $"{RuntimeInformation.ProcessArchitecture} (64-bit: {Environment.Is64BitProcess})";
        OsText.Text = RuntimeInformation.OSDescription;

        var memoryMb = Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0);
        MemoryText.Text = $"{memoryMb:F1} MB (Private working set)";
    }

    private async Task LoadSnapshotAsync(INativeCoreClient core)
    {
        try
        {
            var snapshot = await core.GetSnapshotAsync();
            _dispatcherQueue?.TryEnqueue(() => ApplySnapshot(snapshot));
        }
        catch { }
    }

    private void OnSnapshotChanged(object? sender, NativeCoreSnapshot snapshot) =>
        _dispatcherQueue?.TryEnqueue(() => ApplySnapshot(snapshot));

    private void ApplySnapshot(NativeCoreSnapshot snapshot)
    {
        DeviceNameText.Text = Environment.MachineName;
        DeviceIdText.Text = snapshot.ConnectedDevice != null
            ? $"Connected Peer: {snapshot.ConnectedDevice.Name} ({snapshot.ConnectedDevice.Id})"
            : $"{Environment.MachineName} (Awaiting peer connection)";
        FingerprintText.Text = snapshot.HostListening ? "Active TLS 1.3 Server (TCP Port 47800)" : "TLS server starting…";
        FirewallText.Text = snapshot.FirewallAllowed ? "Allowed in Windows Firewall" : snapshot.FirewallStatus;

        var memoryMb = Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0);
        MemoryText.Text = $"{memoryMb:F1} MB (Private working set)";
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TwinDesk.Native", "Logs");
            Directory.CreateDirectory(dataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dataDirectory}\"") { UseShellExecute = true });
        }
        catch { }
    }
}
