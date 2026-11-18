using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TwinDesk.Native.Core;
using TwinDesk.Native.Core.Discovery;
using TwinDesk.Native.Services;

namespace TwinDesk.Native;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\TwinDesk.Native.Singleton";
    private const string ActivationPipeName = "TwinDesk.Native.Activation";

    private Window? _window;
    private NativeCoreClient? _core;
    private NativeIdentity? _identity;
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private DispatcherQueue? _dispatcherQueue;
    private CancellationTokenSource? _activationStop;
    private Task? _activationTask;
    private int _shutdownStarted;

    public App()
    {
        InitializeComponent();

        try
        {
            _instanceMutex = new Mutex(true, InstanceMutexName, out _ownsInstanceMutex);
        }
        catch (AbandonedMutexException)
        {
            _ownsInstanceMutex = true;
        }

        UnhandledException += (_, args) =>
            NativeFileLogger.Error("Unhandled WinUI application exception", args.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            NativeFileLogger.Error(
                $"Unhandled application-domain exception (terminating={args.IsTerminating})",
                args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            NativeFileLogger.Error("Unobserved background task exception", args.Exception);
            args.SetObserved();
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!_ownsInstanceMutex)
        {
            await ActivateExistingInstanceAsync();
            _instanceMutex?.Dispose();
            _instanceMutex = null;
            Exit();
            return;
        }

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TwinDesk.Native");

        NativeFileLogger.Initialize(Path.Combine(dataDirectory, "Logs"));
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _activationStop = new CancellationTokenSource();
        _activationTask = ListenForActivationAsync(_activationStop.Token);
        NativeFileLogger.Information("Starting native Windows client.");

        try
        {
            var identity = new NativeIdentityStore(dataDirectory).LoadOrCreate();
            _identity = identity;
            _core = new NativeCoreClient(identity, dataDirectory, new DiscoveryOptions
            {
                DeviceId = identity.Id,
                DeviceName = identity.Name,
                Fingerprint = identity.Fingerprint
            }, new WindowsClipboardAdapter());

            _window = new MainWindow(_core);
            _window.Closed += async (_, _) => await ShutdownAsync();
            _window.Activate();

            await _core.StartAsync();
            NativeFileLogger.Information("Native core startup completed.");
        }
        catch (Exception ex)
        {
            NativeFileLogger.Error("Native startup failed", ex);
            ShowStartupError(ex.Message);
            await ShutdownAsync();
            Exit();
        }
    }

    private async Task ActivateExistingInstanceAsync()
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".", ActivationPipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                await client.ConnectAsync(350);
                using var writer = new StreamWriter(client, Encoding.UTF8, 1024, leaveOpen: true)
                {
                    AutoFlush = true
                };
                await writer.WriteLineAsync("activate");
                return;
            }
            catch (TimeoutException)
            {
                await Task.Delay(150);
            }
            catch (IOException)
            {
                await Task.Delay(150);
            }
        }
    }

    private async Task ListenForActivationAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    ActivationPipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
                var command = await reader.ReadLineAsync(cancellationToken);

                if (string.Equals(command, "activate", StringComparison.Ordinal))
                    _dispatcherQueue?.TryEnqueue(() => _window?.Activate());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    NativeFileLogger.Error("Single-instance activation listener failed", ex);
                    await Task.Delay(250, cancellationToken);
                }
            }
        }
    }

    private async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return;

        NativeFileLogger.Information("Shutting down native Windows client.");

        if (_activationStop is not null)
        {
            await _activationStop.CancelAsync();
            if (_activationTask is not null)
            {
                try { await _activationTask; }
                catch (OperationCanceledException) { }
            }
            _activationStop.Dispose();
            _activationStop = null;
        }

        if (_core is not null)
        {
            try { await _core.DisposeAsync(); }
            catch (Exception ex) { NativeFileLogger.Error("Native core shutdown failed", ex); }
            _core = null;
        }

        _identity?.Dispose();
        _identity = null;

        if (_ownsInstanceMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        _ownsInstanceMutex = false;

        NativeFileLogger.Information("Native Windows client stopped.");
        NativeFileLogger.Shutdown();
    }

    private static void ShowStartupError(string message) =>
        MessageBoxW(IntPtr.Zero,
            "TwinDesk could not initialize its native device identity or host services." +
            Environment.NewLine + Environment.NewLine + message,
            "TwinDesk", 0x10);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
