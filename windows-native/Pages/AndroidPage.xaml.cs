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
                $"Unable to read discovery state: {ex.Message}");
        }
    }

    private void OnSnapshotChanged(object? sender, NativeCoreSnapshot snapshot) =>
        _dispatcherQueue?.TryEnqueue(() => ApplySnapshot(snapshot));

    private void ApplySnapshot(NativeCoreSnapshot snapshot)
    {
        DiscoveryStatusText.Text = snapshot.Status;
        var androidPeers = snapshot.NearbyPeers
            .Where(peer => peer.Platform == PeerPlatform.Android)
            .ToArray();

        DeviceCountText.Text = androidPeers.Length == 1
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
            return;
        }

        foreach (var peer in androidPeers)
        {
            var item = new StackPanel { Spacing = 3 };
            item.Children.Add(new TextBlock { Text = peer.Name, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            item.Children.Add(new TextBlock { Text = $"{peer.Address}:{peer.Port}" });
            item.Children.Add(new TextBlock { Text = $"Last seen {peer.LastSeen.ToLocalTime():HH:mm:ss} · Pairing unavailable", FontSize = 12 });
            AndroidPeersPanel.Children.Add(new Border
            {
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Child = item
            });
        }
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
}
