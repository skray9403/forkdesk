namespace TwinDesk.Native.Core;

/// <summary>
/// Protocol-v1 constants shared with the existing Android implementation.
/// Keep wire values stable. This file deliberately contains only the discovery
/// and session foundation; feature message names are added with their ports.
/// </summary>
public static class ProtocolV1
{
    public const int Version = 1;
    public const int Port = 47800;
    public const int DiscoveryPort = 47801;
    public const int BeaconIntervalMs = 2000;
    public const int ProbeIntervalMs = 10000;
    public const int ProbeReplyTimeoutMs = 1500;
    public const int DeviceStaleMs = 10000;
    public const int PingIntervalMs = 1000;
    public const int SessionTimeoutMs = 8000;
    public const int HelloTimeoutMs = 4000;
    public const int PairTimeoutMs = 60000;
    public const int MaxPayloadBytes = 16 * 1024 * 1024;
    public const string SasLabel = "macshare-sas-v1";

    public static class PeerOs
    {
        public const string Android = "android";
        public const string Windows = "win";
    }

    public static class Message
    {
        public const string Beacon = "beacon";
        public const string Probe = "probe";
        public const string Invite = "invite";
        public const string PairStart = "pair.start";
        public const string PairNonce = "pair.nonce";
        public const string PairReveal = "pair.reveal";
        public const string PairConfirm = "pair.confirm";
        public const string PairOk = "pair.ok";
        public const string PairCancel = "pair.cancel";
        public const string Hello = "hello";
        public const string Welcome = "welcome";
        public const string Error = "error";
        public const string Ping = "ping";
        public const string Pong = "pong";
        public const string Bye = "bye";
    }

    public static class Error
    {
        public const string VersionMismatch = "version_mismatch";
        public const string BadToken = "bad_token";
        public const string PairRefused = "pair_refused";
        public const string PairTimeout = "pair_timeout";
        public const string Busy = "busy";
        public const string Replaced = "replaced";
        public const string Released = "released";
        public const string Asleep = "asleep";
    }
}
