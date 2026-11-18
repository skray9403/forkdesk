namespace TwinDesk.Native.Core.Discovery;

public sealed record DiscoveryOptions
{
    public string DeviceId { get; init; } = Guid.NewGuid().ToString("N");
    public string DeviceName { get; init; } = Environment.MachineName;
    public string Fingerprint { get; init; } = "";
    public int Port { get; init; } = ProtocolV1.Port;
    public TimeSpan BeaconInterval { get; init; } =
        TimeSpan.FromMilliseconds(ProtocolV1.BeaconIntervalMs);
    public TimeSpan ProbeInterval { get; init; } =
        TimeSpan.FromMilliseconds(ProtocolV1.ProbeIntervalMs);
    public TimeSpan StaleAfter { get; init; } =
        TimeSpan.FromMilliseconds(ProtocolV1.DeviceStaleMs);
}
