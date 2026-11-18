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
            return beacon is not null
                && beacon.Version == ProtocolV1.Version
                && !string.IsNullOrWhiteSpace(beacon.Id)
                && PeerPlatformPolicy.TryParseWireId(beacon.Os, out _)
                && beacon.Type is ProtocolV1.Message.Beacon
                    or ProtocolV1.Message.Invite
                    or ProtocolV1.Message.Probe;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
