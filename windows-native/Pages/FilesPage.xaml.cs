using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TwinDesk.Native.Core;

namespace TwinDesk.Native.Pages;

public sealed partial class FilesPage : Page
{
    private INativeCoreClient? _core;
    private DispatcherQueue? _dispatcherQueue;
    private bool _updating;

    public FilesPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_core is not null) _core.SnapshotChanged -= OnSnapshotChanged;
        if (e.Parameter is not INativeCoreClient core)
        {
            SendStatusText.Text = "Native core is not connected to this page.";
            return;
        }

        _core = core;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _core.SnapshotChanged += OnSnapshotChanged;
        _ = LoadSnapshotAsync(core);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_core is not null) _core.SnapshotChanged -= OnSnapshotChanged;
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
            _dispatcherQueue?.TryEnqueue(() => SendStatusText.Text = $"Unable to load files state: {ex.Message}");
        }
    }

    private void OnSnapshotChanged(object? sender, NativeCoreSnapshot snapshot) =>
        _dispatcherQueue?.TryEnqueue(() => ApplySnapshot(snapshot));

    private void ApplySnapshot(NativeCoreSnapshot snapshot)
    {
        _updating = true;
        try
        {
            var files = snapshot.Files;
            if (files is null)
            {
                EnabledCheckBox.IsEnabled = false;
                SendButton.IsEnabled = false;
                BrowseButton.IsEnabled = false;
                SendStatusText.Text = "File transfer service is unavailable.";
                return;
            }

            EnabledCheckBox.IsEnabled = true;
            EnabledCheckBox.IsChecked = files.Options.Enabled;
            DownloadDirText.Text = files.Options.DownloadDirectory ?? "Downloads/TwinDesk";

            var isConnected = snapshot.ConnectedDevice is not null;
            SendButton.IsEnabled = isConnected && files.Options.Enabled;
            BrowseButton.IsEnabled = isConnected && files.Options.Enabled;

            SendStatusText.Text = isConnected
                ? (files.Options.Enabled ? $"Connected to {snapshot.ConnectedDevice?.Name}" : "File transfers are disabled in settings")
                : "No authenticated peer connected. Connect an Android phone to send files.";

            // Render active transfers
            ActiveTransfersPanel.Children.Clear();
            if (files.ActiveTransfers.Count == 0)
            {
                NoActiveTransfersText.Visibility = Visibility.Visible;
            }
            else
            {
                NoActiveTransfersText.Visibility = Visibility.Collapsed;
                foreach (var item in files.ActiveTransfers)
                {
                    ActiveTransfersPanel.Children.Add(CreateActiveTransferCard(item));
                }
            }

            // Render history transfers
            HistoryTransfersPanel.Children.Clear();
            if (files.RecentTransfers.Count == 0)
            {
                NoHistoryText.Visibility = Visibility.Visible;
            }
            else
            {
                NoHistoryText.Visibility = Visibility.Collapsed;
                foreach (var item in files.RecentTransfers)
                {
                    HistoryTransfersPanel.Children.Add(CreateHistoryTransferRow(item));
                }
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private UIElement CreateActiveTransferCard(FileTransferItem item)
    {
        var card = new Border
        {
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(8),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"]
        };

        var panel = new StackPanel { Spacing = 8 };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleBlock = new TextBlock
        {
            Text = $"{(item.Direction == FileTransferDirection.Send ? "Sending" : "Receiving")}: {item.FileName}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        Grid.SetColumn(titleBlock, 0);
        headerGrid.Children.Add(titleBlock);

        var cancelBtn = new Button
        {
            Content = "Cancel",
            FontSize = 12
        };
        cancelBtn.Click += async (_, _) =>
        {
            if (_core is not null)
                await _core.CancelFileTransferAsync(item.Id);
        };
        Grid.SetColumn(cancelBtn, 1);
        headerGrid.Children.Add(cancelBtn);

        panel.Children.Add(headerGrid);

        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = item.ProgressPercent,
            IsIndeterminate = item.TotalBytes <= 0
        };
        panel.Children.Add(progressBar);

        var metaText = new TextBlock
        {
            Text = $"{FormatBytes(item.TransferredBytes)} of {FormatBytes(item.TotalBytes)} ({item.ProgressPercent:F1}%)  •  {item.StatusMessage}",
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        panel.Children.Add(metaText);

        card.Child = panel;
        return card;
    }

    private UIElement CreateHistoryTransferRow(FileTransferItem item)
    {
        var grid = new Grid
        {
            Padding = new Thickness(10, 6, 10, 6)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var infoPanel = new StackPanel { Spacing = 2 };
        var title = new TextBlock
        {
            Text = $"{item.FileName} ({FormatBytes(item.TotalBytes)})",
            FontWeight = Microsoft.UI.Text.FontWeights.Medium
        };
        var status = new TextBlock
        {
            Text = $"{(item.Direction == FileTransferDirection.Send ? "Sent" : "Received")}  •  {item.State}  •  {item.StartedAt.ToLocalTime():g}",
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        infoPanel.Children.Add(title);
        infoPanel.Children.Add(status);
        Grid.SetColumn(infoPanel, 0);
        grid.Children.Add(infoPanel);

        if (!string.IsNullOrWhiteSpace(item.LocalPath) && File.Exists(item.LocalPath))
        {
            var openBtn = new Button
            {
                Content = "Show in Folder",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            openBtn.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.LocalPath}\"") { UseShellExecute = true });
                }
                catch { }
            };
            Grid.SetColumn(openBtn, 1);
            grid.Children.Add(openBtn);
        }

        return grid;
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        var core = _core;
        if (core is null) return;

        var path = FilePathBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SendStatusText.Text = "Please enter or browse to a valid file path.";
            return;
        }

        try
        {
            SendButton.IsEnabled = false;
            SendStatusText.Text = $"Starting transfer for {Path.GetFileName(path)}…";
            await core.SendFileAsync(path);
            FilePathBox.Text = string.Empty;
        }
        catch (Exception ex)
        {
            SendStatusText.Text = $"Send error: {ex.Message}";
        }
        finally
        {
            SendButton.IsEnabled = true;
        }
    }

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var hwnd = GetActiveWindow();
            if (hwnd != IntPtr.Zero)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }
            picker.FileTypeFilter.Add("*");
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                FilePathBox.Text = file.Path;
            }
        }
        catch (Exception ex)
        {
            SendStatusText.Text = $"File picker unavailable: {ex.Message}. You can manually paste the file path.";
        }
    }

    private async void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        var core = _core;
        if (_updating || core is null) return;

        var options = new FileTransferOptions(Enabled: EnabledCheckBox.IsChecked == true);
        try
        {
            await core.SetFileTransferOptionsAsync(options);
            ApplySnapshot(await core.GetSnapshotAsync());
        }
        catch (Exception ex)
        {
            SendStatusText.Text = $"Could not update transfer options: {ex.Message}";
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();
}
