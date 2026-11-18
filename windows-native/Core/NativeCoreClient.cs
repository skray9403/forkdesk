using TwinDesk.Native.Core.Discovery;

namespace TwinDesk.Native.Core;

/// <summary>
/// First native core slice: live Windows/Android UDP discovery.
/// Pairing and authenticated session methods remain deliberately unavailable
/// until their protocol state machines have been ported and tested.
/// </summary>
public sealed class NativeCoreClient : INativeCoreClient
{
    private readonly PeerDiscoveryService _discovery;
    private bool _disposed;

    public event EventHandler<NativeCoreSnapshot>? SnapshotChanged;

    public NativeCoreClient(DiscoveryOptions? options = null)
    {
        _discovery = new PeerDiscoveryService(options ?? new DiscoveryOptions());
        _discovery.PeersChanged += (_, _) => Publish();
        _discovery.StatusChanged += (_, status) => Publish(status);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _discovery.StartAsync(cancellationToken).ConfigureAwait(false);
        Publish("Listening for Windows and Android peers");
    }

    public Task<NativeCoreSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new NativeCoreSnapshot(true, "Discovery active", _discovery.Snapshot()));
    }

    public Task RefreshDiscoveryAsync(CancellationToken cancellationToken = default) =>
        _discovery.RefreshAsync(cancellationToken);

    public Task PairAsync(string peerId, CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException(
            "Pairing is not enabled until the authenticated protocol-v1 pairing flow is ported."));

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException(
            "Sessions are not enabled until the authenticated protocol-v1 transport is ported."));

    private void Publish(string? status = null) =>
        SnapshotChanged?.Invoke(this, new NativeCoreSnapshot(
            true, status ?? "Discovery active", _discovery.Snapshot()));

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _discovery.DisposeAsync().ConfigureAwait(false);
    }
}
