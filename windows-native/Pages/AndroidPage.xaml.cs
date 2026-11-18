using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TwinDesk.Native.Core;

namespace TwinDesk.Native.Pages;

public sealed partial class AndroidPage : Page
{
    private INativeCoreClient? _core;
    private DispatcherQueue? _dispatcherQueue;

    public AndroidPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not INativeCoreClient core)
        {
            DiscoveryStatusText.Text = "Native discovery is not connected to this page.";
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
            _dispatcherQueue?.TryEnqueue(() => DiscoveryStatusText.Text =
                $"Unable to read native state: {ex.Message}");
        }
    }

    private void OnSnapshotChanged(object? sender, NativeCoreSnapshot snapshot) =>
        _dispatcherQueue?.TryEnqueue(() => ApplySnapshot(snapshot));

    private void ApplySnapshot(NativeCoreSnapshot snapshot)
    {
        DiscoveryStatusText.Text = $"{snapshot.Status}. {snapshot.HostStatus}";
        FirewallInfoBar.IsOpen = !snapshot.FirewallAllowed;
        FirewallInfoBar.Message = snapshot.FirewallStatus;
        FixFirewallButton.IsEnabled = !snapshot.FirewallAllowed;
        var androidPeers = snapshot.NearbyPeers
            .Where(peer => peer.Platform == PeerPlatform.Android)
            .ToArray();

        DeviceCountText.Text = snapshot.ConnectedDevice is not null
            ? $"Connected to {snapshot.ConnectedDevice.Name}"
            : androidPeers.Length == 1
                ? "1 Android device nearby"
                : $"{androidPeers.Length} Android devices nearby";

        AndroidPeersPanel.Children.Clear();
        if (androidPeers.Length == 0)
        {
            AndroidPeersPanel.Children.Add(new TextBlock
            {
                Text = "No Android device found. Keep TwinDesk open on your phone and check that both devices are on the same network.",
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            foreach (var peer in androidPeers)
            {
                var item = new StackPanel { Spacing = 3 };
                item.Children.Add(new TextBlock { Text = peer.Name, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                item.Children.Add(new TextBlock { Text = $"{peer.Address}:{peer.Port}" });
                item.Children.Add(new TextBlock { Text = $"Last seen {peer.LastSeen.ToLocalTime():HH:mm:ss} · {(peer.IsPaired ? "Paired" : "Not paired")}", FontSize = 12 });
                AndroidPeersPanel.Children.Add(new Border
                {
                    Padding = new Thickness(14),
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(1),
                    Child = item
                });
            }
        }

        var prompt = snapshot.PendingPairing;
        PairingPromptCard.Visibility = prompt is null ? Visibility.Collapsed : Visibility.Visible;
        if (prompt is null) return;

        PairingPromptTitle.Text = $"{prompt.DeviceName} ({prompt.Platform}) wants to pair";
        PairingCodeText.Text = prompt.ShortAuthenticationCode.Length == 6
            ? $"{prompt.ShortAuthenticationCode[..3]} {prompt.ShortAuthenticationCode[3..]}"
            : "Waiting…";
        PairingStatusText.Text = prompt.IsConfirmed
            ? "The phone confirmed the code. Approve here to finish pairing."
            : "Compare both codes. Approving grants this device a persistent pairing token.";
        AllowPairingButton.IsEnabled = prompt.ShortAuthenticationCode.Length == 6 && !prompt.IsAllowed;
        DenyPairingButton.IsEnabled = true;
    }

    private async void RefreshDiscovery_Click(object sender, RoutedEventArgs e)
    {
        var core = _core;
        if (core is null) return;

        RefreshButton.IsEnabled = false;
        try
        {
            await core.RefreshDiscoveryAsync();
            ApplySnapshot(await core.GetSnapshotAsync());
        }
        catch (Exception ex)
        {
            DiscoveryStatusText.Text = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            RefreshButton.IsEnabled = _core is not null;
        }
    }

    private async void FixFirewall_Click(object sender, RoutedEventArgs e)
    {
        var core = _core;
        if (core is null) return;

        FixFirewallButton.IsEnabled = false;
        FirewallInfoBar.Message = "Windows will ask for administrator approval to add only the TwinDesk TCP/UDP listener rules.";
        try
        {
            await core.FixFirewallAsync();
            var snapshot = await core.GetSnapshotAsync();
            FirewallInfoBar.IsOpen = !snapshot.FirewallAllowed;
            FirewallInfoBar.Message = snapshot.FirewallStatus;
        }
        catch (Exception ex)
        {
            FirewallInfoBar.Message = $"Firewall repair was not completed: {ex.Message}";
            FixFirewallButton.IsEnabled = true;
        }
    }

    private async void ApprovePairing_Click(object sender, RoutedEventArgs e)
    {
        var core = _core;
        if (core is null) return;
        AllowPairingButton.IsEnabled = false;
        try { await core.ApprovePairingAsync(true); }
        catch (Exception ex) { PairingStatusText.Text = $"Approval failed: {ex.Message}"; }
    }

    private async void DenyPairing_Click(object sender, RoutedEventArgs e)
    {
        var core = _core;
        if (core is null) return;
        DenyPairingButton.IsEnabled = false;
        try { await core.ApprovePairingAsync(false); }
        catch (Exception ex) { PairingStatusText.Text = $"Denial failed: {ex.Message}"; }
    }
}
