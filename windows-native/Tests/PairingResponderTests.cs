using TwinDesk.Native.Core;
using Xunit;

namespace TwinDesk.Native.Tests;

public sealed class PairingResponderTests
{
    private const string HostId = "host-device-id";
    private const string HostName = "Test Windows PC";
    private const string Fingerprint = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
    private const string WindowsNonce = "000102030405060708090a0b0c0d0e0f";
    private const string RemoteAddress = "192.168.1.22";

    [Fact]
    public async Task PairingRequiresPhoneConfirmationAndLocalApproval()
    {
        var store = new MemoryStore();
        await using var responder = Create(store);
        var messages = new List<PairingWireMessage>();
        PairingPrompt? prompt = null;
        responder.MessageReady += messages.Add;
        responder.PromptChanged += value => prompt = value;

        responder.Begin("socket-1", RemoteAddress, Request());
        Assert.Equal(ProtocolV1.Message.PairNonce, messages.Single().Type);
        Assert.Equal(32, Assert.IsType<string>(messages[0].Fields["nonce"]).Length);

        responder.Reveal("socket-1", WindowsNonce);
        Assert.Equal("187236", prompt?.ShortAuthenticationCode);
        Assert.False(prompt?.IsConfirmed);
        Assert.False(prompt?.IsAllowed);

        responder.Approve(true);
        Assert.Empty(store.ReadAll());
        Assert.Single(messages);

        responder.Confirm("socket-1");
        Assert.Equal(ProtocolV1.Message.PairOk, messages.Last().Type);
        var paired = store.ReadAll().Single();
        Assert.True(ProtocolCrypto.TokenMatches(Assert.IsType<string>(messages.Last().Fields["token"]), paired.TokenHash));
        Assert.Equal(HostId, messages.Last().Fields["macId"]);
        Assert.Null(responder.Pending);
    }

    [Fact]
    public async Task PairingCompletesWhenPhoneConfirmsBeforeLocalApproval()
    {
        var store = new MemoryStore();
        await using var responder = Create(store);
        var messages = new List<PairingWireMessage>();
        responder.MessageReady += messages.Add;

        responder.Begin("socket-1", RemoteAddress, Request());
        responder.Reveal("socket-1", WindowsNonce);
        responder.Confirm("socket-1");
        Assert.Empty(store.ReadAll());

        responder.Approve(true);
        Assert.Single(store.ReadAll());
        Assert.Equal(ProtocolV1.Message.PairOk, messages.Last().Type);
    }

    [Fact]
    public async Task WrongNonceAbortsAndRecordsFailure()
    {
        var store = new MemoryStore();
        await using var responder = Create(store);
        var messages = new List<PairingWireMessage>();
        responder.MessageReady += messages.Add;

        responder.Begin("socket-1", RemoteAddress, Request());
        responder.Reveal("socket-1", "ffffffffffffffffffffffffffffffff");

        Assert.Equal(ProtocolV1.Message.Error, messages.Last().Type);
        Assert.Equal(ProtocolV1.Error.PairRefused, messages.Last().Fields["code"]);
        Assert.Null(responder.Pending);
        Assert.False(responder.IsAddressLocked(RemoteAddress));
    }

    [Fact]
    public async Task FifthFailedAttemptLocksAddress()
    {
        var store = new MemoryStore();
        await using var responder = Create(store);
        var messages = new List<PairingWireMessage>();
        responder.MessageReady += messages.Add;

        for (var i = 0; i < PairingResponder.MaxFailuresPerAddress; i++)
        {
            var connectionId = $"socket-{i}";
            responder.Begin(connectionId, RemoteAddress, Request());
            responder.Reveal(connectionId, "ffffffffffffffffffffffffffffffff");
        }

        Assert.True(responder.IsAddressLocked(RemoteAddress));
        responder.Begin("socket-blocked", RemoteAddress, Request());
        Assert.Equal(ProtocolV1.Error.PairRefused, messages.Last().Fields["code"]);
    }

    [Fact]
    public async Task ApproveBeforeSasDoesNotAuthorizePairing()
    {
        var store = new MemoryStore();
        await using var responder = Create(store);
        responder.Begin("socket-1", RemoteAddress, Request());
        responder.Approve(true);
        Assert.Empty(store.ReadAll());
        Assert.Equal(string.Empty, responder.Pending?.ShortAuthenticationCode);
    }

    [Fact]
    public async Task OtherSocketCannotRevealAnActivePairing()
    {
        var store = new MemoryStore();
        await using var responder = Create(store);
        var messages = new List<PairingWireMessage>();
        responder.MessageReady += messages.Add;

        responder.Begin("socket-1", RemoteAddress, Request());
        responder.Reveal("socket-2", WindowsNonce);

        Assert.Equal(ProtocolV1.Message.Error, messages.Last().Type);
        Assert.Empty(store.ReadAll());
    }

    [Fact]
    public async Task BusyHostRejectsPairing()
    {
        var store = new MemoryStore();
        await using var responder = new PairingResponder(HostId, HostName, Fingerprint, store, () => true);
        var messages = new List<PairingWireMessage>();
        responder.MessageReady += messages.Add;

        responder.Begin("socket-1", RemoteAddress, Request());

        Assert.Equal(ProtocolV1.Error.Busy, messages.Single().Fields["code"]);
        Assert.Null(responder.Pending);
    }

    [Fact]
    public async Task PairedDeviceStorePersistsOnlyTokenHashesAndSupportsForget()
    {
        var root = Path.Combine(Path.GetTempPath(), "TwinDesk.Native.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "paired.json");
        try
        {
            var store = new JsonPairedDeviceStore(path);
            var now = DateTimeOffset.UtcNow;
            var device = new PairedDevice("phone-1", "Pixel", "android", ProtocolCrypto.Sha256Hex("secret-token"), now, now);
            store.Upsert(device);

            Assert.Equal(device, new JsonPairedDeviceStore(path).ReadAll().Single());
            Assert.DoesNotContain("secret-token", File.ReadAllText(path));
            Assert.True(store.Remove("phone-1"));
            Assert.Empty(store.ReadAll());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static PairingResponder Create(IPairedDeviceStore store) =>
        new(HostId, HostName, Fingerprint, store);

    private static PairingStartRequest Request() =>
        new(ProtocolV1.Version, "android-device-id", "Pixel 8a", "android",
            ProtocolCrypto.CommitOf(WindowsNonce));

    private sealed class MemoryStore : IPairedDeviceStore
    {
        private readonly List<PairedDevice> _devices = [];
        public IReadOnlyList<PairedDevice> ReadAll() => _devices.ToArray();
        public void Upsert(PairedDevice device) { _devices.RemoveAll(x => x.Id == device.Id); _devices.Add(device); }
        public bool Remove(string deviceId) => _devices.RemoveAll(x => x.Id == deviceId) > 0;
    }
}
