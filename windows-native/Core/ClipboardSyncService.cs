using System.Text;
using System.Text.Json;

namespace TwinDesk.Native.Core;

public sealed record ClipboardSyncOptions(bool Enabled = false, bool Send = true, bool Receive = true);

public sealed record ClipboardSyncSnapshot(
    ClipboardSyncOptions Options,
    bool IsConnected,
    bool PeerAccepts,
    string Status,
    DateTimeOffset? LastSentAt,
    DateTimeOffset? LastReceivedAt);

public interface IClipboardAdapter : IDisposable
{
    event EventHandler? ContentChanged;
    Task<string?> ReadTextAsync(CancellationToken cancellationToken = default);
    Task WriteTextAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>Text-only protocol-v1 clipboard sync. Image and file clipboard parity is separate work.</summary>
public sealed class ClipboardSyncService : IDisposable
{
    private readonly object _sync = new();
    private readonly IClipboardAdapter _adapter;
    private readonly string _settingsPath;
    private readonly Func<string, CancellationToken, Task<bool>> _sendMessage;
    private readonly SemaphoreSlim _localChangeGate = new(1, 1);
    private ClipboardSyncOptions _options;
    private bool _isConnected;
    private bool _peerAccepts;
    private string? _lastSentText;
    private string? _lastReceivedText;
    private DateTimeOffset? _lastSentAt;
    private DateTimeOffset? _lastReceivedAt;
    private string _status = "Clipboard sync is off";
    private bool _disposed;

    public event EventHandler<ClipboardSyncSnapshot>? SnapshotChanged;

    public ClipboardSyncService(IClipboardAdapter adapter, string dataDirectory,
        Func<string, CancellationToken, Task<bool>> sendMessage)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _sendMessage = sendMessage ?? throw new ArgumentNullException(nameof(sendMessage));
        Directory.CreateDirectory(dataDirectory);
        _settingsPath = Path.Combine(dataDirectory, "clipboard.json");
        _options = LoadOptions(_settingsPath);
        _adapter.ContentChanged += OnLocalClipboardChanged;
    }

    public ClipboardSyncSnapshot Snapshot
    {
        get
        {
            lock (_sync)
                return new(_options, _isConnected, _peerAccepts, _status, _lastSentAt, _lastReceivedAt);
        }
    }

    public async Task SetOptionsAsync(ClipboardSyncOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _options = options;
            _status = BuildStatusLocked();
        }
        SaveOptions(_settingsPath, options);
        await SendPolicyAsync(cancellationToken).ConfigureAwait(false);
        Publish();
    }

    public async Task SetSessionActiveAsync(bool connected, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_isConnected == connected) return;
            _isConnected = connected;
            _peerAccepts = connected;
            _lastSentText = null;
            _lastReceivedText = null;
            _lastSentAt = null;
            _lastReceivedAt = null;
            _status = BuildStatusLocked();
        }
        if (connected)
            await SendPolicyAsync(cancellationToken).ConfigureAwait(false);
        Publish();
    }

    public async Task HandlePeerMessageAsync(string type, JsonElement message,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        switch (type)
        {
            case ProtocolV1.Message.ClipPolicy:
            {
                var accepts = message.TryGetProperty("accept", out var accepted) &&
                              accepted.ValueKind == JsonValueKind.True;
                lock (_sync)
                {
                    _peerAccepts = accepts;
                    _status = BuildStatusLocked();
                }
                Publish();
                break;
            }
            case ProtocolV1.Message.Clip:
            {
                if (!message.TryGetProperty("text", out var textElement) ||
                    textElement.ValueKind != JsonValueKind.String)
                    break;
                var text = textElement.GetString() ?? string.Empty;
                if (Encoding.UTF8.GetByteCount(text) > ProtocolV1.ClipboardMaxBytes)
                    break;

                lock (_sync)
                {
                    if (!_isConnected || !_options.Enabled || !_options.Receive ||
                        string.Equals(_lastReceivedText, text, StringComparison.Ordinal))
                        break;
                    _lastReceivedText = text;
                    _lastSentText = null;
                    _lastReceivedAt = DateTimeOffset.UtcNow;
                    _status = "Received text from the connected device";
                }
                await _adapter.WriteTextAsync(text, cancellationToken).ConfigureAwait(false);
                Publish();
                break;
            }
            case ProtocolV1.Message.ClipRequest:
                await SendLocalClipboardAsync(cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private void OnLocalClipboardChanged(object? sender, EventArgs e)
    {
        if (!_disposed) _ = SendLocalClipboardSafelyAsync();
    }

    private async Task SendLocalClipboardSafelyAsync()
    {
        try { await SendLocalClipboardAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_sync) _status = $"Clipboard read failed: {ex.Message}";
            Publish();
        }
    }

    private async Task SendLocalClipboardAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
            if (!_isConnected || !_options.Enabled || !_options.Send || !_peerAccepts) return;

        await _localChangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var text = await _adapter.ReadTextAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(text)) return;
            if (Encoding.UTF8.GetByteCount(text) > ProtocolV1.ClipboardMaxBytes)
            {
                lock (_sync) _status = $"Clipboard text exceeds {ProtocolV1.ClipboardMaxBytes / 1024} KB";
                Publish();
                return;
            }

            lock (_sync)
                if (!_isConnected || !_options.Enabled || !_options.Send || !_peerAccepts ||
                    string.Equals(_lastSentText, text, StringComparison.Ordinal) ||
                    string.Equals(_lastReceivedText, text, StringComparison.Ordinal))
                    return;

            var payload = JsonSerializer.Serialize(new { t = ProtocolV1.Message.Clip, text });
            if (!await _sendMessage(payload, cancellationToken).ConfigureAwait(false)) return;

            lock (_sync)
            {
                _lastSentText = text;
                _lastReceivedText = null;
                _lastSentAt = DateTimeOffset.UtcNow;
                _status = "Sent text to the connected device";
            }
            Publish();
        }
        finally { _localChangeGate.Release(); }
    }

    private async Task SendPolicyAsync(CancellationToken cancellationToken)
    {
        bool connected;
        bool accept;
        lock (_sync)
        {
            connected = _isConnected;
            accept = _options.Enabled && _options.Receive;
        }
        if (!connected) return;

        var payload = JsonSerializer.Serialize(new
        {
            t = ProtocolV1.Message.ClipPolicy,
            accept,
            images = false
        });
        await _sendMessage(payload, cancellationToken).ConfigureAwait(false);
    }

    private string BuildStatusLocked()
    {
        if (!_options.Enabled) return "Clipboard sync is off";
        if (!_isConnected) return "Enabled, waiting for a connected device";
        if (!_options.Send && !_options.Receive) return "Connected, but both clipboard directions are paused";
        if (!_options.Send) return "Receiving text from the connected device";
        if (!_options.Receive) return "Sending text to the connected device";
        if (!_peerAccepts) return "The connected device is not accepting clipboard text";
        return "Text clipboard sync is active";
    }

    private void Publish() => SnapshotChanged?.Invoke(this, Snapshot);

    private static ClipboardSyncOptions LoadOptions(string path)
    {
        if (!File.Exists(path)) return new ClipboardSyncOptions();
        try { return JsonSerializer.Deserialize<ClipboardSyncOptions>(File.ReadAllBytes(path)) ?? new(); }
        catch (JsonException) { return new ClipboardSyncOptions(); }
        catch (IOException) { return new ClipboardSyncOptions(); }
    }

    private static void SaveOptions(string path, ClipboardSyncOptions options)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(options));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _adapter.ContentChanged -= OnLocalClipboardChanged;
        _adapter.Dispose();
        _localChangeGate.Dispose();
        SnapshotChanged = null;
    }
}
