using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Text;

namespace TwinDesk.Native.Pages;

public sealed class FeaturePage : Page
{
    private static readonly IReadOnlyDictionary<string, (string Title, string Description)> Features =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["files"] = ("Files", "Send and receive files between this PC and an Android device."),
            ["clipboard"] = ("Clipboard", "Synchronize text and supported clipboard content."),
            ["screen"] = ("Screen", "View or share a screen using the native capture pipeline."),
            ["remote"] = ("Remote input", "Use the phone as a touchpad, keyboard or remote control."),
            ["audio"] = ("Audio", "Route audio between the PC and connected Android device."),
            ["notifications"] = ("Notifications", "View and act on Android notifications."),
            ["about"] = ("About TwinDesk", "A native Windows client focused on Android and Windows devices.")
        };

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var key = e.Parameter as string ?? "about";
        var item = Features.TryGetValue(key, out var value) ? value : ("TwinDesk", "Native Windows client.");
        var content = new StackPanel
        {
            Padding = new Thickness(32),
            Spacing = 16,
            MaxWidth = 900,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        content.Children.Add(new TextBlock
        {
            Text = item.Item1,
            FontSize = 30,
            FontWeight = FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = item.Item2,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            Title = "Native feature migration pending",
            Message = "This is a native WinUI surface. Its feature service will be enabled after the corresponding Windows and Android protocol path is connected."
        });
        Content = new ScrollViewer { Content = content };
    }
}
