namespace TwinDesk.Native.Core;

/// <summary>UI-facing boundary for native Windows session and peer services.</summary>
public interface INativeCoreClient : IAsyncDisposable
{
    event EventHandler<NativeCoreSnapshot>? SnapshotChanged;
    Task<NativeCoreSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task RefreshDiscoveryAsync(CancellationToken cancellationToken = default);
    Task PairAsync(string peerId, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
