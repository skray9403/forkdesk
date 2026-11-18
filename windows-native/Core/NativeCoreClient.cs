using System.Net.Sockets;
using TwinDesk.Native.Core.Discovery;

namespace TwinDesk.Native.Core;

public sealed class NativeCoreClient : INativeCoreClient
{
    private readonly PeerDiscoveryService _discovery;
    private readonly NativePeerHost _host;
    private readonly NativeFirewallAccessService _firewall = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;
    private bool _disposed;
    private string _status = "Starting native services";
    private string _hostStatus = "Host server has not started";
    private FirewallAccessStatus _firewallStatus = new(false, "Checking Windows Firewall access…");

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

            _firewallStatus = await Task.Run(() => _firewall.Check(), cancellationToken).ConfigureAwait(false);
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

    public async Task FixFirewallAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _firewallStatus = await _firewall.EnsureAsync(AppContext.BaseDirectory, cancellationToken)
            .ConfigureAwait(false);
        Publish();
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _host.DisconnectAsync(cancellationToken);
    }

    private void OnHostStateChanged() => Publish();

    private NativeCoreSnapshot CreateSnapshot()
    {
        var pairedIds = _host.PairedDevices.Select(device => device.Id)
            .ToHashSet(StringComparer.Ordinal);
        var connectedId = _host.ConnectedDevice?.Id;
        var nearbyPeers = _discovery.Snapshot()
            .Select(peer => peer with
            {
                IsPaired = pairedIds.Contains(peer.Id),
                IsConnected = string.Equals(peer.Id, connectedId, StringComparison.Ordinal)
            })
            .ToArray();

        return new NativeCoreSnapshot(
            _started, _status, nearbyPeers, _host.IsListening, _hostStatus,
            _host.PendingPairing, _host.PairedDevices, _host.ConnectedDevice,
            _firewallStatus.IsAllowed, _firewallStatus.Message);
    }

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
