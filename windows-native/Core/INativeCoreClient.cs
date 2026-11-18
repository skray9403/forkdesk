namespace TwinDesk.Native.Core;

public interface INativeCoreClient : IAsyncDisposable
{
    event EventHandler<NativeCoreSnapshot>? SnapshotChanged;
    Task<NativeCoreSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task RefreshDiscoveryAsync(CancellationToken cancellationToken = default);
    Task PairAsync(string peerId, CancellationToken cancellationToken = default);
    Task ApprovePairingAsync(bool allow, CancellationToken cancellationToken = default);
    Task<bool> ForgetDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
    Task SetClipboardOptionsAsync(ClipboardSyncOptions options, CancellationToken cancellationToken = default);
    Task FixFirewallAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
