namespace TwinDesk.Native.Core;

public sealed record NearbyPeer(
    string Id, string Name, PeerPlatform Platform, string Address, int Port,
    string Fingerprint, bool IsPaired, bool IsConnected, DateTimeOffset LastSeen);

public sealed record NativeCoreSnapshot(
    bool IsAvailable,
    string Status,
    IReadOnlyList<NearbyPeer> NearbyPeers,
    bool HostListening = false,
    string HostStatus = "",
    PairingPrompt? PendingPairing = null,
    IReadOnlyList<PairedDevice>? PairedDevices = null,
    PairedDevice? ConnectedDevice = null,
    bool FirewallAllowed = false,
    string FirewallStatus = "",
    ClipboardSyncSnapshot? Clipboard = null,
    FileTransferSnapshot? Files = null,
    RemoteInputSnapshot? RemoteInput = null,
    NotificationSnapshot? Notifications = null,
    ScreenMirrorSnapshot? ScreenMirror = null,
    AudioRoutingSnapshot? AudioRouting = null)
{
    public static NativeCoreSnapshot Unavailable(string status) =>
        new(false, status, Array.Empty<NearbyPeer>());
}
