using System.Net;
using System.Net.Sockets;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using TwinDesk.Native.Core;
using Windows.ApplicationModel.DataTransfer;

namespace TwinDesk.Native.Pages;

public sealed partial class OverviewPage : Page
{
    private INativeCoreClient? _core;
    private DispatcherQueue? _dispatcherQueue;
    private string _localIp = "127.0.0.1";

    public OverviewPage()
    {
        InitializeComponent();
        HostDeviceNameText.Text = Environment.MachineName;
        _localIp = GetLocalIpAddress();
        HostIpPillText.Text = _localIp;
        ThisPcAddressText.Text = _localIp;
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                {
                    return ip.ToString();
                }
            }
        }
        catch { }
        return "127.0.0.1";
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not INativeCoreClient core)
        {
            DiscoveryStatusText.Text = "Native core service is not connected.";
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
            _dispatcherQueue?.TryEnqueue(() => DiscoveryStatusText.Text = $"Snapshot error: {ex.Message}");
        }
    }

    private void OnSnapshotChanged(object? sender, NativeCoreSnapshot snapshot) =>
        _dispatcherQueue?.TryEnqueue(() => ApplySnapshot(snapshot));

    private void ApplySnapshot(NativeCoreSnapshot snapshot)
    {
        // 1. Connection Hero Card state
        var isConnected = snapshot.ConnectedDevice is not null;
        var isWired = snapshot.AdbWired?.ConnectedSerials.Count > 0;

        if (isConnected && snapshot.ConnectedDevice is not null)
        {
            PhoneNameText.Text = snapshot.ConnectedDevice.Name;
            DisconnectButton.Visibility = Visibility.Visible;

            // Connection Link Beam
            HeroLinkBeam.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 48, 209, 88)); // Green
            HeroPulseDot.Visibility = Visibility.Visible;

            // Hero Status Pill
            HeroStatusDot.Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 48, 209, 88));
            var linkType = isWired ? "USB · 4 ms" : "Wi-Fi";
            HeroStatusText.Text = $"Connected · {linkType}";
            HeroSubtext.Text = $"Ultra-low latency {linkType} session established with {snapshot.ConnectedDevice.Name}.";

            // Battery / State
            if (snapshot.Phone is not null && snapshot.Phone.Battery.HasValue)
            {
                var chg = snapshot.Phone.Charging ? "⚡ " : "";
                PhoneBatteryText.Text = $"{chg}{snapshot.Phone.Battery.Value}%";
            }
            else
            {
                PhoneBatteryText.Text = "Connected";
            }

            RingPhoneButton.IsEnabled = true;
            LockPhoneButton.IsEnabled = true;
            RingPhoneButton.Content = snapshot.Phone?.IsRinging == true ? "Stop ringing" : "Ring phone";
        }
        else
        {
            PhoneNameText.Text = "Android Phone";
            PhoneBatteryText.Text = "Waiting for phone";
            DisconnectButton.Visibility = Visibility.Collapsed;

            HeroLinkBeam.Background = (Brush)Application.Current.Resources["SystemFillColorNeutralBrush"];
            HeroPulseDot.Visibility = Visibility.Collapsed;

            HeroStatusDot.Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 161, 161, 166));
            HeroStatusText.Text = "Ready to connect";
            HeroSubtext.Text = snapshot.NearbyPeers.Count > 0
                ? $"{snapshot.NearbyPeers.Count} compatible device(s) found on network."
                : "Connect via USB cable or ensure phone is on same Wi-Fi.";

            RingPhoneButton.IsEnabled = false;
            LockPhoneButton.IsEnabled = false;
            RingPhoneButton.Content = "Ring phone";
        }

        // 2. USB Status Card
        if (snapshot.AdbWired is not null)
        {
            UsbStatusText.Text = snapshot.AdbWired.Status;
        }

        // 3. Nearby Peers Card
        PeerCountText.Text = snapshot.NearbyPeers.Count.ToString();
        DiscoveryStatusText.Text = snapshot.Status;

        NearbyPeersPanel.Children.Clear();
        if (snapshot.NearbyPeers.Count == 0)
        {
            NearbyPeersPanel.Children.Add(new TextBlock
            {
                Text = "No nearby devices discovered yet. Verify Wi-Fi network or plug in USB cable.",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            foreach (var peer in snapshot.NearbyPeers)
            {
                NearbyPeersPanel.Children.Add(CreatePeerRow(peer));
            }
        }

        // 4. Firewall Card
        FirewallStatusText.Text = snapshot.FirewallStatus;
        FixFirewallButton.Visibility = snapshot.FirewallAllowed ? Visibility.Collapsed : Visibility.Visible;
    }

    private UIElement CreatePeerRow(NearbyPeer peer)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Device Icon
        var iconBorder = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["SystemFillColorNeutralBrush"],
            VerticalAlignment = VerticalAlignment.Center
        };
        var glyph = peer.Platform == PeerPlatform.Android ? "\uE8EA" : "\uE7F8";
        iconBorder.Child = new FontIcon
        {
            Glyph = glyph,
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(iconBorder, 0);
        row.Children.Add(iconBorder);

        // Device Details
        var details = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(new TextBlock
        {
            Text = peer.Name,
            FontSize = 13.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        details.Children.Add(new TextBlock
        {
            Text = $"{peer.Platform} · {peer.Address}:{peer.Port} · Last seen {peer.LastSeen.ToLocalTime():HH:mm:ss}",
            FontSize = 11.5,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        Grid.SetColumn(details, 1);
        row.Children.Add(details);

        // Action Button
        var actionBtn = new Button
        {
            Content = peer.IsPaired ? "Connect" : "Pair",
            Tag = peer.Id,
            VerticalAlignment = VerticalAlignment.Center
        };
        actionBtn.Click += PeerPair_Click;
        Grid.SetColumn(actionBtn, 2);
        row.Children.Add(actionBtn);

        return new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Child = row
        };
    }

    private async void PeerPair_Click(object sender, RoutedEventArgs e)
    {
        var core = _core;
        if (core is null || sender is not Button { Tag: string peerId } button)
            return;

        button.IsEnabled = false;
        button.Content = "Inviting…";
        try
        {
            await core.PairAsync(peerId);
        }
        catch (Exception ex)
        {
            DiscoveryStatusText.Text = $"Pair invite failed: {ex.Message}";
            button.IsEnabled = true;
            button.Content = "Pair";
        }
    }

    private async void ConnectAddress_Click(object sender, RoutedEventArgs e)
    {
        var core = _core;
        if (core is null) return;
        var address = DirectAddressInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(address)) return;

        ConnectAddressButton.IsEnabled = false;
        ConnectAddressButton.Content = "Connecting…";
        try
        {
            await core.PairByAddressAsync(address);
        }
        catch (Exception ex)
        {
            DiscoveryStatusText.Text = $"Connection failed: {ex.Message}";
        }
        finally
        {
            ConnectAddressButton.IsEnabled = true;
            ConnectAddressButton.Content = "Connect";
        }
    }

    private void DirectAddressInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ConnectAddress_Click(sender, e);
        }
    }

    private void CopyIp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dp = new DataPackage();
            dp.SetText(_localIp);
            Clipboard.SetContent(dp);
        }
        catch { }
    }

    private async void ScanUsb_Click(object sender, RoutedEventArgs e)
    {
        var core = _core;
        if (core is null) return;
        ScanUsbButton.IsEnabled = false;
        try
        {
            await core.TriggerWiredScanAsync();
        }
        finally
        {
            ScanUsbButton.IsEnabled = true;
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var core = _core;
        if (core is null) return;
        await core.DisconnectAsync();
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

    private async void RingPhone_Click(object sender, RoutedEventArgs e)
    {
        var core = _core;
        if (core is null) return;
        var snapshot = await core.GetSnapshotAsync();
        var ringing = snapshot.Phone?.IsRinging == true;
        await core.RingPhoneAsync(!ringing);
    }

    private async void LockPhone_Click(object sender, RoutedEventArgs e)
    {
        var core = _core;
        if (core is null) return;
        await core.LockPhoneAsync();
    }

    private void StartMirror_Click(object sender, RoutedEventArgs e)
    {
        _core?.StartScreenMirroring();
    }

    private async void FixFirewall_Click(object sender, RoutedEventArgs e)
    {
        var core = _core;
        if (core is null) return;
        try
        {
            await core.FixFirewallAsync();
            ApplySnapshot(await core.GetSnapshotAsync());
        }
        catch { }
    }
}
