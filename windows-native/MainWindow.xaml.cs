using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TwinDesk.Native.Core;
using TwinDesk.Native.Pages;

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
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        ContentFrame.Navigate(typeof(OverviewPage), _core);
        _core.SnapshotChanged += OnCoreSnapshotChanged;
        Closed += (_, _) => _core.SnapshotChanged -= OnCoreSnapshotChanged;
    }

    private void OnCoreSnapshotChanged(object? sender, NativeCoreSnapshot snapshot)
    {
        if (snapshot.PendingPairing is null)
            return;

        _dispatcherQueue.TryEnqueue(() =>
        {
            var androidItem = RootNavigation.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(item => Equals(item.Tag, "android"));
            if (androidItem is not null && !Equals(RootNavigation.SelectedItem, androidItem))
                RootNavigation.SelectedItem = androidItem;

            Activate();
        });
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
