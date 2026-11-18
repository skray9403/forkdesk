namespace TwinDesk.Native.Core;

public sealed record NearbyPeer(
    string Id, string Name, PeerPlatform Platform, string Address, bool IsPaired, bool IsConnected);

public sealed record NativeCoreSnapshot(
    bool IsAvailable, string Status, IReadOnlyList<NearbyPeer> NearbyPeers);

/// <summary>
/// UI-facing boundary for the native Windows session engine. UI pages must not call Electron,
/// Node or platform-specific peer implementations directly.
/// </summary>
public interface INativeCoreClient : IAsyncDisposable
{
    event EventHandler<NativeCoreSnapshot>? SnapshotChanged;
    Task<NativeCoreSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task RefreshDiscoveryAsync(CancellationToken cancellationToken = default);
    Task PairAsync(string peerId, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
