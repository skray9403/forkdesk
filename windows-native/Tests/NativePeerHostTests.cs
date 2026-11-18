using System.Net.WebSockets;
using System.Text.Json;
using TwinDesk.Native.Core;
using Xunit;

namespace TwinDesk.Native.Tests;

public sealed class NativePeerHostTests
{
    [Fact]
    public async Task AndroidPeerCanPairAndAuthenticateOverPinnedTlsWebSocket()
    {
        var root = Path.Combine(Path.GetTempPath(), "TwinDesk.Native.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var identity = new NativeIdentityStore(root).LoadOrCreate("Test Windows PC");
        await using var host = new NativePeerHost(identity, root, port: 0);
        using var client = new ClientWebSocket();
        client.Options.RemoteCertificateValidationCallback = static (_, _, _, _) => true;

        try
        {
            await host.StartAsync();
            Assert.InRange(host.Port, 1, 65535);
            await client.ConnectAsync(new Uri($"wss://127.0.0.1:{host.Port}/"), CancellationToken.None);

            const string phoneId = "android-test-device";
            const string phoneName = "Pixel test phone";
            var phoneNonce = ProtocolCrypto.RandomHex(16);
            await SendAsync(client, new
            {
                t = ProtocolV1.Message.PairStart,
                v = ProtocolV1.Version,
                id = phoneId,
                name = phoneName,
                os = ProtocolV1.PeerOs.Android,
                commit = ProtocolCrypto.CommitOf(phoneNonce)
            });

            using var nonceMessage = await ReceiveAsync(client);
            Assert.Equal(ProtocolV1.Message.PairNonce, nonceMessage.RootElement.GetProperty("t").GetString());
            var hostNonce = nonceMessage.RootElement.GetProperty("nonce").GetString()!;
            Assert.Equal(32, hostNonce.Length);

            await SendAsync(client, new { t = ProtocolV1.Message.PairReveal, nonce = phoneNonce });
            await WaitUntilAsync(() => host.PendingPairing?.ShortAuthenticationCode.Length == 6);
            var expectedCode = ProtocolCrypto.SasCode(identity.Fingerprint, phoneNonce, hostNonce);
            Assert.Equal(expectedCode, host.PendingPairing?.ShortAuthenticationCode);

            await SendAsync(client, new { t = ProtocolV1.Message.PairConfirm });
            await WaitUntilAsync(() => host.PendingPairing?.IsConfirmed == true);
            host.ApprovePairing(true);

            using var pairOk = await ReceiveAsync(client);
            Assert.Equal(ProtocolV1.Message.PairOk, pairOk.RootElement.GetProperty("t").GetString());
            var token = pairOk.RootElement.GetProperty("token").GetString()!;
            Assert.Equal(64, token.Length);

            await SendAsync(client, new
            {
                t = ProtocolV1.Message.Hello,
                v = ProtocolV1.Version,
                id = phoneId,
                name = phoneName,
                os = ProtocolV1.PeerOs.Android,
                token
            });

            using var welcome = await ReceiveAsync(client);
            Assert.Equal(ProtocolV1.Message.Welcome, welcome.RootElement.GetProperty("t").GetString());
            Assert.Equal(identity.Id, welcome.RootElement.GetProperty("macId").GetString());
            Assert.Equal(ProtocolV1.PeerOs.Windows, welcome.RootElement.GetProperty("os").GetString());
            Assert.Equal(phoneId, host.ConnectedDevice?.Id);

            await SendAsync(client, new { t = ProtocolV1.Message.Ping, ts = 123456L });
            using var pong = await ReceiveUntilAsync(client,
                rootElement => rootElement.TryGetProperty("t", out var t) &&
                               t.GetString() == ProtocolV1.Message.Pong);
            Assert.Equal(123456L, pong.RootElement.GetProperty("ts").GetInt64());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SendAsync<T>(ClientWebSocket socket, T payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text,
            endOfMessage: true, CancellationToken.None);
    }

    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket) =>
        await ReceiveUntilAsync(socket, _ => true);

    private static async Task<JsonDocument> ReceiveUntilAsync(
        ClientWebSocket socket,
        Func<JsonElement, bool> predicate)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new IOException("WebSocket closed while a test response was expected.");
                stream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var document = JsonDocument.Parse(stream.ToArray());
            if (predicate(document.RootElement))
                return document;
            document.Dispose();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "Timed out waiting for the native peer host state.");
    }
}
