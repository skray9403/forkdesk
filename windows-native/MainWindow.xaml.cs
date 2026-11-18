using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TwinDesk.Native.Core;
using TwinDesk.Native.Pages;

namespace TwinDesk.Native;

public sealed partial class MainWindow : Window
{
    private readonly INativeCoreClient _core;

    public MainWindow(INativeCoreClient core)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        InitializeComponent();
        Title = "TwinDesk";
        SystemBackdrop = new MicaBackdrop();
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        ContentFrame.Navigate(typeof(OverviewPage), _core);
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag) return;
        switch (tag)
        {
            case "overview": ContentFrame.Navigate(typeof(OverviewPage), _core); break;
            case "android": ContentFrame.Navigate(typeof(AndroidPage), _core); break;
            case "settings": ContentFrame.Navigate(typeof(SettingsPage)); break;
            default: ContentFrame.Navigate(typeof(FeaturePage), tag); break;
        }
    }
}
