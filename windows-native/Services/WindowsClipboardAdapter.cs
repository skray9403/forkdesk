using Microsoft.UI.Dispatching;
using TwinDesk.Native.Core;
using Windows.ApplicationModel.DataTransfer;

namespace TwinDesk.Native.Services;

public sealed class WindowsClipboardAdapter : IClipboardAdapter
{
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _disposed;

    public event EventHandler? ContentChanged;

    public WindowsClipboardAdapter()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("A WinUI dispatcher is required for clipboard access.");
        Clipboard.ContentChanged += OnClipboardContentChanged;
    }

    public Task<string?> ReadTextAsync(CancellationToken cancellationToken = default) =>
        InvokeOnUiThreadAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
                return null;
            return await content.GetTextAsync();
        }, cancellationToken);

    public async Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        await InvokeOnUiThreadAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return Task.FromResult(true);
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task<T> InvokeOnUiThreadAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        if (_dispatcherQueue.HasThreadAccess)
            return operation();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(await operation());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }))
            completion.TrySetException(new InvalidOperationException("The WinUI dispatcher is shutting down."));

        return completion.Task;
    }

    private void OnClipboardContentChanged(object sender, object args) =>
        ContentChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clipboard.ContentChanged -= OnClipboardContentChanged;
        ContentChanged = null;
    }
}
