namespace TwinDesk.Native.Core;

public sealed record NearbyPeer(
    string Id, string Name, PeerPlatform Platform, string Address, int Port,
    string Fingerprint, bool IsPaired, bool IsConnected, DateTimeOffset LastSeen);

public sealed record PhoneTelemetry(
    int? Battery = null,
    bool Charging = false,
    string? Model = null,
    bool IsRinging = false);

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
    AudioRoutingSnapshot? AudioRouting = null,
    AdbWiredSnapshot? AdbWired = null,
    PhoneTelemetry? Phone = null,
    EdgeCrossingSnapshot? EdgeCrossing = null)
{
    public static NativeCoreSnapshot Unavailable(string status) =>
        new(false, status, Array.Empty<NearbyPeer>());
}

public sealed record AndroidAppInfo(string PackageName, string DisplayName);

