using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TwinDesk.Native.Core;
using TwinDesk.Native.Pages;
using Windows.Graphics;

namespace TwinDesk.Native;

public sealed partial class MainWindow : Window
{
    private readonly INativeCoreClient _core;
    private readonly DispatcherQueue _dispatcherQueue;

    public MainWindow(INativeCoreClient core)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();
        Title = "TwinDesk";
        SystemBackdrop = new MicaBackdrop();

        SetPreferredWindowSize(960, 700);

        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        ContentFrame.Navigate(typeof(OverviewPage), _core);
        _core.SnapshotChanged += OnCoreSnapshotChanged;
        Closed += (_, _) => _core.SnapshotChanged -= OnCoreSnapshotChanged;

        _ = LoadInitialSnapshotAsync();
    }

    private void SetPreferredWindowSize(int width, int height)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        if (appWindow is not null)
        {
            appWindow.Resize(new SizeInt32(width, height));
        }
    }

    private async Task LoadInitialSnapshotAsync()
    {
        try
        {
            var snapshot = await _core.GetSnapshotAsync();
            _dispatcherQueue.TryEnqueue(() => UpdateChrome(snapshot));
        }
        catch
        {
            // Initial load best-effort
        }
    }

    private void OnCoreSnapshotChanged(object? sender, NativeCoreSnapshot snapshot)
    {
        _dispatcherQueue.TryEnqueue(() => UpdateChrome(snapshot));
    }

    private void UpdateChrome(NativeCoreSnapshot snapshot)
    {
        // 1. Sidebar status indicator & text
        if (snapshot.ConnectedDevice is not null)
        {
            SidebarStatusDot.Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 48, 209, 88)); // Apple/Win Green
            var isWired = snapshot.AdbWired?.ConnectedSerials.Count > 0;
            var pingText = isWired ? " · USB 4 ms" : "";
            SidebarStatusText.Text = $"Connected to {snapshot.ConnectedDevice.Name}{pingText}";

            // 2. Sidebar connected phone tile
            SidebarPhoneTile.Visibility = Visibility.Visible;
            SidebarPhoneName.Text = snapshot.ConnectedDevice.Name;
            var bat = snapshot.Phone?.Battery;
            var chg = snapshot.Phone?.Charging == true ? " ⚡" : "";
            var linkType = isWired ? "USB link" : "Wi-Fi link";
            SidebarPhoneSub.Text = bat.HasValue ? $"{bat.Value}%{chg} · {linkType}" : linkType;
        }
        else
        {
            SidebarPhoneTile.Visibility = Visibility.Collapsed;
            var peerCount = snapshot.NearbyPeers.Count;
            if (peerCount > 0)
            {
                SidebarStatusDot.Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 10, 132, 255)); // Blue
                SidebarStatusText.Text = $"{peerCount} peer(s) found";
            }
            else
            {
                SidebarStatusDot.Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 161, 161, 166)); // Grey
                SidebarStatusText.Text = "Not paired";
            }
        }

        // 3. Top sticky pairing banner
        if (snapshot.PendingPairing is not null)
        {
            PairingBanner.Visibility = Visibility.Visible;
            var sas = snapshot.PendingPairing.ShortAuthenticationCode;
            PairingSasCode.Text = sas.Length >= 6 ? $"{sas[..3]}  {sas[3..]}" : sas;
            PairingHintText.Text = $"Pairing request from {snapshot.PendingPairing.DeviceName} ({snapshot.PendingPairing.DeviceId}).";
            Activate();
        }
        else
        {
            PairingBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void SidebarPhoneTile_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var phoneItem = RootNavigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => Equals(i.Tag, "android"));
        if (phoneItem is not null)
            RootNavigation.SelectedItem = phoneItem;
    }

    private async void ApprovePairing_Click(object sender, RoutedEventArgs e)
    {
        try { await _core.ApprovePairingAsync(true); } catch { }
    }

    private async void RejectPairing_Click(object sender, RoutedEventArgs e)
    {
        try { await _core.ApprovePairingAsync(false); } catch { }
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag) return;
        switch (tag)
        {
            case "overview": ContentFrame.Navigate(typeof(OverviewPage), _core); break;
            case "android": ContentFrame.Navigate(typeof(AndroidPage), _core); break;
            case "files": ContentFrame.Navigate(typeof(FilesPage), _core); break;
            case "clipboard": ContentFrame.Navigate(typeof(ClipboardPage), _core); break;
            case "screen": ContentFrame.Navigate(typeof(ScreenPage), _core); break;
            case "remote": ContentFrame.Navigate(typeof(RemotePage), _core); break;
            case "audio": ContentFrame.Navigate(typeof(AudioPage), _core); break;
            case "notifications": ContentFrame.Navigate(typeof(NotificationsPage), _core); break;
            case "settings": ContentFrame.Navigate(typeof(SettingsPage)); break;
            case "about": ContentFrame.Navigate(typeof(AboutPage), _core); break;
            default: ContentFrame.Navigate(typeof(OverviewPage), _core); break;
        }
    }
}
