using TwinDesk.Native.Core.Discovery;

namespace TwinDesk.Native.Core;

/// <summary>
/// First native core slice: live Windows/Android UDP discovery.
/// Pairing and authenticated session methods deliberately remain unavailable
/// until their protocol state machines have been ported and tested.
/// </summary>
public sealed class NativeCoreClient : INativeCoreClient
{
    private readonly PeerDiscoveryService _discovery;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;
    private bool _disposed;
    private string _status = "Starting native discovery";

    public event EventHandler<NativeCoreSnapshot>? SnapshotChanged;

    public NativeCoreClient(DiscoveryOptions? options = null)
    {
        _discovery = new PeerDiscoveryService(options ?? new DiscoveryOptions());
        _discovery.PeersChanged += (_, _) => Publish();
        _discovery.StatusChanged += (_, status) => Publish(status);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            try
            {
                await _discovery.StartAsync(cancellationToken).ConfigureAwait(false);
                _started = true;
                _status = "Listening for Windows and Android peers";
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException)
            {
                _status = $"Discovery unavailable: {ex.Message}";
                Publish();
                throw;
            }
            Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<NativeCoreSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new NativeCoreSnapshot(_started, _status, _discovery.Snapshot()));
    }

    public Task RefreshDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
            return Task.FromException(new InvalidOperationException("Native discovery has not started."));
        return _discovery.RefreshAsync(cancellationToken);
    }

    public Task PairAsync(string peerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(peerId))
            return Task.FromException(new ArgumentException("A peer ID is required.", nameof(peerId)));
        return Task.FromException(new NotSupportedException(
            "Pairing remains disabled until protocol-v1 identity, TLS and SAS confirmation are ported."));
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(new NotSupportedException(
            "Sessions remain disabled until the authenticated protocol-v1 transport is ported."));
    }

    private void Publish(string? status = null)
    {
        if (!string.IsNullOrWhiteSpace(status))
            _status = status;
        SnapshotChanged?.Invoke(this, new NativeCoreSnapshot(
            _started, _status, _discovery.Snapshot()));
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _started = false;
            _status = "Stopped";
            await _discovery.DisposeAsync().ConfigureAwait(false);
            Publish();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
