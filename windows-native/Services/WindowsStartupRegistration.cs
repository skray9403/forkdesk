using Microsoft.Win32;

namespace TwinDesk.Native.Services;

public static class WindowsStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TwinDesk";

    public static bool IsEnabled()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;

        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var registeredCommand = runKey?.GetValue(ValueName) as string;
        return string.Equals(
            registeredCommand,
            QuoteExecutable(Path.GetFullPath(executablePath)),
            StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current user's startup registry key.");

        if (!enabled)
        {
            runKey.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            throw new InvalidOperationException("Could not locate the current TwinDesk executable.");

        runKey.SetValue(
            ValueName,
            QuoteExecutable(Path.GetFullPath(executablePath)),
            RegistryValueKind.String);
    }

    private static string QuoteExecutable(string executablePath) => $"\"{executablePath}\"";
}
