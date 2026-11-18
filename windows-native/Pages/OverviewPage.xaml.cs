using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace TwinDesk.Native.Pages;
public sealed partial class OverviewPage : Page
{
    public OverviewPage() => InitializeComponent();

    private void OpenAndroid_Click(object sender, RoutedEventArgs e)
    {
        if (Frame?.Parent is NavigationView navigation)
        {
            foreach (var item in navigation.MenuItems.OfType<NavigationViewItem>())
            {
                if (Equals(item.Tag, "android")) { navigation.SelectedItem = item; return; }
            }
        }
        Frame?.Navigate(typeof(AndroidPage));
    }
}
