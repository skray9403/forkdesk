using System.Net.Sockets;
using TwinDesk.Native.Core.Discovery;

namespace TwinDesk.Native.Core;

public sealed class NativeCoreClient : INativeCoreClient
{
    private readonly PeerDiscoveryService _discovery;
    private readonly NativePeerHost _host;
    private readonly NativeFirewallAccessService _firewall = new();
    private readonly ClipboardSyncService? _clipboard;
    private readonly FileTransferService _files;
    private readonly NativeRemoteInputService _remoteInput;
    private readonly NativeNotificationService _notifications;
    private readonly NativeScreenMirrorService _screenMirror = new();
    private readonly NativeAudioRoutingService _audio;
    private readonly NativeAdbWiredService _wired;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;
    private bool _disposed;
    private string _status = "Starting native services";
    private string _hostStatus = "Host server has not started";
    private FirewallAccessStatus _firewallStatus = new(false, "Checking Windows Firewall access…");

    public event EventHandler<NativeCoreSnapshot>? SnapshotChanged;

    public NativeCoreClient(NativeIdentity identity, string dataDirectory, DiscoveryOptions? options = null, IClipboardAdapter? clipboardAdapter = null)
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
        _host.AuthenticatedMessageReceived += HandlePeerMessageAsync;
        _files = new FileTransferService(dataDirectory, identity.Id, SendPeerMessageAsync);
        _files.SnapshotChanged += (_, _) => Publish();
        if (clipboardAdapter is not null)
        {
            _clipboard = new ClipboardSyncService(clipboardAdapter, dataDirectory, identity.Id, SendPeerMessageAsync);
            _clipboard.SnapshotChanged += (_, _) => Publish();
        }

        _remoteInput = new NativeRemoteInputService(SendPeerMessageAsync);
        _remoteInput.SnapshotChanged += _ => Publish();

        _notifications = new NativeNotificationService(SendPeerMessageAsync);
        _notifications.SnapshotChanged += _ => Publish();

        _audio = new NativeAudioRoutingService(SendPeerMessageAsync);
        _audio.SnapshotChanged += _ => Publish();

        _screenMirror.SnapshotChanged += _ => Publish();

        _wired = new NativeAdbWiredService(identity.Id, identity.Name, discoveryOptions.Port, () => _host.ConnectedDevice is not null);
        _wired.SnapshotChanged += _ => Publish();
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
            _wired.Start();
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

    public async Task PairAsync(string peerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(peerId))
            throw new ArgumentException("A peer ID is required.", nameof(peerId));

        var snapshot = _discovery.Snapshot();
        var peer = snapshot.FirstOrDefault(p => string.Equals(p.Id, peerId, StringComparison.OrdinalIgnoreCase));
        var target = peer?.Address ?? peerId.Trim();
        await _discovery.SendInviteToAddressAsync(target, cancellationToken).ConfigureAwait(false);
    }

    public async Task PairByAddressAsync(string address, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("An IP address or hostname is required.", nameof(address));

        await _discovery.SendInviteToAddressAsync(address.Trim(), cancellationToken).ConfigureAwait(false);
    }

    public Task ApprovePairingAsync(bool allow, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _host.ApprovePairing(allow);
        return Task.CompletedTask;
    }

    public async Task SetClipboardOptionsAsync(ClipboardSyncOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_clipboard is null)
            throw new InvalidOperationException("The Windows clipboard adapter is not available.");
        await _clipboard.SetOptionsAsync(options, cancellationToken).ConfigureAwait(false);
        Publish();
    }

    public Task<string> SendFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _files.StartSendFileAsync(filePath, cancellationToken);
    }

    public Task CancelFileTransferAsync(string transferId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _files.CancelTransferAsync(transferId, cancellationToken);
    }

    public async Task SetFileTransferOptionsAsync(FileTransferOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _files.SetOptionsAsync(options, cancellationToken).ConfigureAwait(false);
        Publish();
    }

    public Task SetRemoteInputOptionsAsync(RemoteInputOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _remoteInput.SetOptions(options);
        return Task.CompletedTask;
    }

    public Task SetNotificationEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _notifications.SetEnabledAsync(enabled, cancellationToken);
    }

    public Task DismissNotificationAsync(string id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _notifications.DismissNotificationAsync(id, cancellationToken);
    }

    public void ClearNotifications()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _notifications.ClearAll();
    }

    public Task SetAudioRoutingOptionsAsync(AudioRoutingOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _audio.SetOptionsAsync(options, cancellationToken);
    }

    public void SetScreenMirrorOptions(ScreenMirrorOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _screenMirror.SetOptions(options);
    }

    public bool StartScreenMirroring(string? deviceAddress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(deviceAddress))
        {
            var connected = _host.ConnectedDevice;
            if (connected is not null)
            {
                var peer = _discovery.Snapshot().FirstOrDefault(p => p.Id == connected.Id);
                deviceAddress = peer?.Address;
            }
        }
        return _screenMirror.StartMirroring(deviceAddress);
    }

    public void StopScreenMirroring()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _screenMirror.StopMirroring();
    }

    public Task TriggerWiredScanAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _wired.TriggerScanAsync(cancellationToken);
    }

    private Task<bool> SendPeerMessageAsync(string message, CancellationToken cancellationToken) =>
        _host.SendToActiveSessionAsync(message, cancellationToken);

    private async Task HandlePeerMessageAsync(PairedDevice device, string type, System.Text.Json.JsonElement message)
    {
        if (_clipboard is not null)
            await _clipboard.HandlePeerMessageAsync(type, message).ConfigureAwait(false);
        await _files.HandlePeerMessageAsync(type, message).ConfigureAwait(false);
        await _remoteInput.HandlePeerMessageAsync(type, message, CancellationToken.None).ConfigureAwait(false);
        await _notifications.HandlePeerMessageAsync(type, message, CancellationToken.None).ConfigureAwait(false);
        await _audio.HandlePeerMessageAsync(type, message, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task UpdateSessionStatesAsync(bool connected)
    {
        if (_clipboard is not null)
        {
            try { await _clipboard.SetSessionActiveAsync(connected).ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Clipboard session update failed: {ex}"); }
        }

        try { await _files.SetSessionActiveAsync(connected).ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"File transfer session update failed: {ex}"); }

        if (connected)
        {
            try { await _remoteInput.HandleSessionStartedAsync().ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Remote input session update failed: {ex}"); }

            try { await _notifications.HandleSessionStartedAsync().ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Notifications session update failed: {ex}"); }

            try { await _audio.HandleSessionStartedAsync().ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Audio session update failed: {ex}"); }
        }
    }

    public async Task<bool> ForgetDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var removed = await _host.ForgetDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
        Publish();
        return removed;
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

    private void OnHostStateChanged()
    {
        _ = UpdateSessionStatesAsync(_host.ConnectedDevice is not null);
        Publish();
    }

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
            _firewallStatus.IsAllowed, _firewallStatus.Message, _clipboard?.Snapshot,
            _files.Snapshot, _remoteInput.Snapshot, _notifications.Snapshot,
            _screenMirror.Snapshot, _audio.Snapshot, _wired.Snapshot);
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
            _clipboard?.Dispose();
            _files.Dispose();
            _screenMirror.Dispose();
            await _wired.DisposeAsync().ConfigureAwait(false);
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
