using System.Net.Sockets;
using TwinDesk.Native.Core.Discovery;

namespace TwinDesk.Native.Core;

/// <summary>
/// Coordinates the native Windows host, protocol-v1 pairing and local-network discovery.
/// Feature messages are accepted by the host boundary but are not enabled until native
/// feature services are ported.
/// </summary>
public sealed class NativeCoreClient : INativeCoreClient
{
    private readonly PeerDiscoveryService _discovery;
    private readonly NativePeerHost _host;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;
    private bool _disposed;
    private string _status = "Starting native services";
    private string _hostStatus = "Host server has not started";

    public event EventHandler<NativeCoreSnapshot>? SnapshotChanged;

    public NativeCoreClient(NativeIdentity identity, string dataDirectory, DiscoveryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        var discoveryOptions = options ?? new DiscoveryOptions
        {
            DeviceId = identity.Id,
            DeviceName = identity.Name,
            Fingerprint = identity.Fingerprint
        };

        _discovery = new PeerDiscoveryService(discoveryOptions);
        _host = new NativePeerHost(identity, dataDirectory, discoveryOptions.Port);
        _discovery.PeersChanged += (_, _) => Publish();
        _discovery.StatusChanged += (_, status) => Publish(status);
        _host.StateChanged += OnHostStateChanged;
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
                await _host.StartAsync(cancellationToken).ConfigureAwait(false);
                _hostStatus = $"Listening for authenticated peers on TCP {ProtocolV1.Port}";
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException)
            {
                _hostStatus = $"Host unavailable: {ex.Message}";
                _status = _hostStatus;
                Publish();
                return;
            }

            try
            {
                await _discovery.StartAsync(cancellationToken).ConfigureAwait(false);
                _started = true;
                _status = "Native host and Windows/Android discovery are active";
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException)
            {
                _status = $"Host is listening, but discovery is unavailable: {ex.Message}";
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
        return Task.FromResult(CreateSnapshot());
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
            "The Android app initiates protocol-v1 pairing. Approve its SAS request on this PC when it appears."));
    }

    public Task ApprovePairingAsync(bool allow, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _host.ApprovePairing(allow);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _host.DisconnectAsync(cancellationToken);
    }

    private void OnHostStateChanged() => Publish();

    private NativeCoreSnapshot CreateSnapshot() => new(
        _started,
        _status,
        _discovery.Snapshot(),
        _host.IsListening,
        _hostStatus,
        _host.PendingPairing,
        _host.PairedDevices,
        _host.ConnectedDevice);

    private void Publish(string? status = null)
    {
        if (!string.IsNullOrWhiteSpace(status))
            _status = status;
        SnapshotChanged?.Invoke(this, CreateSnapshot());
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
            await _host.DisposeAsync().ConfigureAwait(false);
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
