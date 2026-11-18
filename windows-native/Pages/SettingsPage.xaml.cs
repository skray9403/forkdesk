using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TwinDesk.Native.Services;

namespace TwinDesk.Native.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _updating;

    public SettingsPage()
    {
        InitializeComponent();
        LoadStartupState();

        var logPath = NativeFileLogger.LogFilePath;
        LogPathText.Text = string.IsNullOrWhiteSpace(logPath)
            ? "Log file is created in the TwinDesk local application data folder."
            : $"Log file: {logPath}";
    }

    private void LoadStartupState()
    {
        try
        {
            _updating = true;
            StartupToggle.IsOn = WindowsStartupRegistration.IsEnabled();
            StartupStatusText.Text = StartupToggle.IsOn
                ? "Enabled for the current Windows user."
                : "Disabled. TwinDesk will start only when you open it.";
            StartupPathText.Text = $"Executable: {Environment.ProcessPath ?? "Unavailable"}";
        }
        catch (Exception ex)
        {
            StartupToggle.IsEnabled = false;
            StartupStatusText.Text = $"Startup preference is unavailable: {ex.Message}";
            StartupPathText.Text = string.Empty;
        }
        finally
        {
            _updating = false;
        }
    }

    private void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updating)
            return;

        var previousValue = !StartupToggle.IsOn;
        try
        {
            WindowsStartupRegistration.SetEnabled(StartupToggle.IsOn);
            StartupStatusText.Text = StartupToggle.IsOn
                ? "TwinDesk will launch automatically when this Windows user signs in."
                : "Automatic sign-in launch is disabled.";
            StartupPathText.Text = $"Registered executable: {Environment.ProcessPath ?? "Unavailable"}";
        }
        catch (Exception ex)
        {
            _updating = true;
            StartupToggle.IsOn = previousValue;
            _updating = false;
            StartupStatusText.Text = $"Could not update the startup preference: {ex.Message}";
        }
    }
}
