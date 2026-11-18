namespace TwinDesk.Native.Core;

/// <summary>Peer platforms supported by the native Windows edition.</summary>
public enum PeerPlatform
{
    Unknown = 0,
    Android = 1,
    Windows = 2,
    MacOS = 3,
    Linux = 4,
    IOS = 5
}

public static class PeerPlatformPolicy
{
    public static bool IsSupported(PeerPlatform platform) =>
        platform is PeerPlatform.Android or PeerPlatform.Windows;
}
