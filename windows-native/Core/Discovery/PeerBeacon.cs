using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwinDesk.Native.Core.Discovery;

internal sealed record PeerBeacon
{
    [JsonPropertyName("t")] public string Type { get; init; } = ProtocolV1.Message.Beacon;
    [JsonPropertyName("v")] public int Version { get; init; } = ProtocolV1.Version;
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("os")] public string Os { get; init; } = ProtocolV1.PeerOs.Windows;
    [JsonPropertyName("role")] public string Role { get; init; } = "host";
    [JsonPropertyName("port")] public int Port { get; init; } = ProtocolV1.Port;
    [JsonPropertyName("fp")] public string Fingerprint { get; init; } = "";

    public static bool TryParse(ReadOnlySpan<byte> utf8, out PeerBeacon? beacon)
    {
        beacon = null;
        try
        {
            beacon = JsonSerializer.Deserialize<PeerBeacon>(utf8);
            if (beacon is null || beacon.Version != ProtocolV1.Version ||
                string.IsNullOrWhiteSpace(beacon.Id))
                return false;

            // A probe is a request, not a peer record. Android uses role=guest for its
            // ordinary computer discovery beacon. Ignore phone-to-phone advertisements
            // and any platform outside this Windows product's allow-list.
            if (beacon.Type == ProtocolV1.Message.Probe)
                return true;

            if (beacon.Type is not (ProtocolV1.Message.Beacon or ProtocolV1.Message.Invite))
                return false;

            if (!PeerPlatformPolicy.TryParseWireId(beacon.Os, out _))
                return false;

            if (beacon.Os == ProtocolV1.PeerOs.Android &&
                !string.Equals(beacon.Role, "guest", StringComparison.Ordinal))
                return false;

            if (beacon.Os == ProtocolV1.PeerOs.Windows &&
                !string.Equals(beacon.Role, "host", StringComparison.Ordinal))
                return false;

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
