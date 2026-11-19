namespace TwinDesk.Native.Core;

public interface INativeCoreClient : IAsyncDisposable
{
    event EventHandler<NativeCoreSnapshot>? SnapshotChanged;
    Task<NativeCoreSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task RefreshDiscoveryAsync(CancellationToken cancellationToken = default);
    Task PairAsync(string peerId, CancellationToken cancellationToken = default);
    Task PairByAddressAsync(string address, CancellationToken cancellationToken = default);
    Task ApprovePairingAsync(bool allow, CancellationToken cancellationToken = default);
    Task<bool> ForgetDeviceAsync(string deviceId, CancellationToken cancellationToken = default);
    Task SetClipboardOptionsAsync(ClipboardSyncOptions options, CancellationToken cancellationToken = default);
    Task<string> SendFileAsync(string filePath, CancellationToken cancellationToken = default);
    Task CancelFileTransferAsync(string transferId, CancellationToken cancellationToken = default);
    Task SetFileTransferOptionsAsync(FileTransferOptions options, CancellationToken cancellationToken = default);
    Task FixFirewallAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task SetRemoteInputOptionsAsync(RemoteInputOptions options, CancellationToken cancellationToken = default);
    Task SetNotificationEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    Task DismissNotificationAsync(string id, CancellationToken cancellationToken = default);
    void ClearNotifications();
    Task SetAudioRoutingOptionsAsync(AudioRoutingOptions options, CancellationToken cancellationToken = default);
    void SetScreenMirrorOptions(ScreenMirrorOptions options);
    bool StartScreenMirroring(string? deviceAddress = null);
    void StopScreenMirroring();
}
