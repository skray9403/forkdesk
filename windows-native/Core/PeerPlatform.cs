namespace TwinDesk.Native.Core;

/// <summary>
/// Peer platforms accepted by the native Windows product.
/// Wire identifiers remain strings because they are part of protocol v1.
/// </summary>
public enum PeerPlatform
{
    Unknown = 0,
    Android = 1,
    Windows = 2
}

/// <summary>
/// Windows-edition policy. This is an application boundary, not a protocol
/// version change: the Android peer continues to speak the existing protocol.
/// </summary>
public static class PeerPlatformPolicy
{
    public static bool IsSupported(PeerPlatform platform) =>
        platform is PeerPlatform.Android or PeerPlatform.Windows;

    public static bool TryParseWireId(string? value, out PeerPlatform platform)
    {
        platform = value?.Trim().ToLowerInvariant() switch
        {
            "android" => PeerPlatform.Android,
            "win" or "windows" => PeerPlatform.Windows,
            _ => PeerPlatform.Unknown
        };

        return IsSupported(platform);
    }

    public static string ToWireId(PeerPlatform platform) => platform switch
    {
        PeerPlatform.Android => "android",
        PeerPlatform.Windows => "win",
        _ => throw new ArgumentOutOfRangeException(nameof(platform),
            "The Windows product cannot advertise or connect to this peer platform.")
    };
}
