using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TwinDesk.Native.Pages;

public sealed partial class OverviewPage : Page
{
    public OverviewPage() => InitializeComponent();

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

        Frame?.Navigate(typeof(AndroidPage));
    }
}
