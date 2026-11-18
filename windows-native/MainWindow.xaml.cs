using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TwinDesk.Native.Pages;
namespace TwinDesk.Native;
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "TwinDesk";
        SystemBackdrop = new MicaBackdrop();
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        ContentFrame.Navigate(typeof(OverviewPage));
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag) return;
        switch (tag)
        {
            case "overview": ContentFrame.Navigate(typeof(OverviewPage)); break;
            case "android": ContentFrame.Navigate(typeof(AndroidPage)); break;
            case "settings": ContentFrame.Navigate(typeof(SettingsPage)); break;
            default: ContentFrame.Navigate(typeof(FeaturePage), tag); break;
        }
    }
}
