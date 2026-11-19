using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TwinDesk.Native.Core;

namespace TwinDesk.Native.Pages;

public sealed partial class NotificationsPage : Page
{
    private INativeCoreClient? _core;
    private DispatcherQueue? _dispatcherQueue;

    public NotificationsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not INativeCoreClient core)
        {
            EmptyNoticeText.Text = "Native core service is not connected to this page.";
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
            _dispatcherQueue?.TryEnqueue(() => EmptyNoticeText.Text = $"Error reading notifications: {ex.Message}");
        }
    }

    private void OnSnapshotChanged(object? sender, NativeCoreSnapshot snapshot) =>
        _dispatcherQueue?.TryEnqueue(() => ApplySnapshot(snapshot));

    private void ApplySnapshot(NativeCoreSnapshot snapshot)
    {
        var notif = snapshot.Notifications;
        var items = notif?.RecentNotifications ?? Array.Empty<PhoneNotification>();

        NotificationsPanel.Children.Clear();
        if (items.Count == 0)
        {
            EmptyNoticeText.Visibility = Visibility.Visible;
            ClearAllButton.IsEnabled = false;
        }
        else
        {
            EmptyNoticeText.Visibility = Visibility.Collapsed;
            ClearAllButton.IsEnabled = true;

            foreach (var item in items)
            {
                var card = new Border
                {
                    Padding = new Thickness(14),
                    CornerRadius = new CornerRadius(8),
                    Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
                    BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1)
                };

                var grid = new Grid { ColumnSpacing = 12 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var content = new StackPanel { Spacing = 4 };
                content.Children.Add(new TextBlock
                {
                    Text = $"{item.AppName} · {item.Timestamp.ToLocalTime():HH:mm:ss}",
                    FontSize = 12,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                });

                if (!string.IsNullOrWhiteSpace(item.Title))
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = item.Title,
                        FontSize = 14,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                    });
                }

                if (!string.IsNullOrWhiteSpace(item.Body))
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = item.Body,
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    });
                }

                var dismissBtn = new Button
                {
                    Content = "Dismiss",
                    Tag = item.Id,
                    VerticalAlignment = VerticalAlignment.Center
                };
                dismissBtn.Click += DismissButton_Click;

                grid.Children.Add(content);
                Grid.SetColumn(dismissBtn, 1);
                grid.Children.Add(dismissBtn);

                card.Child = grid;
                NotificationsPanel.Children.Add(card);
            }
        }
    }

    private void SyncToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_core is null) return;
        _ = _core.SetNotificationEnabledAsync(SyncToggle.IsOn);
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e)
    {
        if (_core is null || sender is not Button { Tag: string id } btn) return;
        btn.IsEnabled = false;
        _ = _core.DismissNotificationAsync(id);
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        _core?.ClearNotifications();
    }
}
