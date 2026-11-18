using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using TwinDesk.Native.Core;

namespace TwinDesk.Native.Pages;

public sealed partial class OverviewPage : Page
{
    private INativeCoreClient? _core;
    private DispatcherQueue? _dispatcherQueue;

    public OverviewPage() => InitializeComponent();

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
        var androidCount = snapshot.NearbyPeers.Count(peer => peer.Platform == PeerPlatform.Android);
        AndroidConnectionState.Text = androidCount == 0
            ? "No Android device discovered"
            : $"{androidCount} Android device(s) discovered";
        PeerCountText.Text = $"{snapshot.NearbyPeers.Count} compatible peer(s) nearby";

        NearbyPeersPanel.Children.Clear();
        if (snapshot.NearbyPeers.Count == 0)
        {
            NearbyPeersPanel.Children.Add(new TextBlock
            {
                Text = "No compatible devices found yet. Check that the phone and PC are on the same network.",
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var peer in snapshot.NearbyPeers)
        {
            var item = new StackPanel { Spacing = 3 };
            item.Children.Add(new TextBlock { Text = peer.Name, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            item.Children.Add(new TextBlock { Text = $"{peer.Platform} · {peer.Address}:{peer.Port}" });
            item.Children.Add(new TextBlock { Text = $"Last seen {peer.LastSeen.ToLocalTime():HH:mm:ss} · Not paired", FontSize = 12 });
            NearbyPeersPanel.Children.Add(new Border
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

    private void OpenAndroid_Click(object sender, RoutedEventArgs e)
    {
        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is NavigationView navigation)
            {
                foreach (var item in navigation.MenuItems.OfType<NavigationViewItem>())
                {
                    if (Equals(item.Tag, "android"))
                    {
                        navigation.SelectedItem = item;
                        return;
                    }
                }
            }

            current = VisualTreeHelper.GetParent(current);
        }

        Frame?.Navigate(typeof(AndroidPage), _core);
    }
}
