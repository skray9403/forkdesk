using System.Diagnostics;
using System.Text;

namespace TwinDesk.Native.Services;

public static class NativeFileLogger
{
    private static readonly object Sync = new();
    private static TextWriterTraceListener? _listener;
    public static string? LogFilePath { get; private set; }

    public static void Initialize(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        lock (Sync)
        {
            if (_listener is not null)
                return;

            Directory.CreateDirectory(logDirectory);
            LogFilePath = Path.Combine(logDirectory, $"TwinDesk-{DateTime.Now:yyyyMMdd}.log");
            var stream = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            _listener = new TextWriterTraceListener(writer, "TwinDeskNativeFile");
            Trace.Listeners.Add(_listener);
            Trace.AutoFlush = true;
            Information($"Diagnostic logging started. Log file: {LogFilePath}");
        }
    }

    public static void Information(string message) => Write("INFO", message, null);
    public static void Warning(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        lock (Sync)
        {
            Trace.WriteLine($"{DateTimeOffset.Now:O} [{level}] {message}");
            if (exception is not null)
                Trace.WriteLine(exception);
            Trace.Flush();
        }
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            if (_listener is null)
                return;

            Trace.WriteLine($"{DateTimeOffset.Now:O} [INFO] Diagnostic logging stopped.");
            Trace.Flush();
            Trace.Listeners.Remove(_listener);
            _listener.Dispose();
            _listener = null;
        }
    }
}
