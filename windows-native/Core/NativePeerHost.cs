using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace TwinDesk.Native.Core;

/// <summary>
/// Hosts the Windows side of protocol v1: Android/Windows peers connect to this
/// machine, pair through the SAS exchange, then authenticate with their token.
/// Feature messages are surfaced for later native service handlers.
/// </summary>
public sealed class NativePeerHost : IAsyncDisposable
{
    private readonly object _sessionSync = new();
    private readonly TlsWebSocketServer _transport;
    private readonly IPairedDeviceStore _pairedStore;
    private readonly PairingResponder _pairing;
    private readonly string _hostId;
    private readonly string _hostName;
    private readonly ConcurrentDictionary<string, PeerSession> _sessions = new(StringComparer.Ordinal);
    private CancellationTokenSource? _heartbeatStop;
    private Task? _heartbeatTask;
    private PeerSession? _active;
    private bool _disposed;

    public event Action? StateChanged;
    public event Func<PairedDevice, string, JsonElement, Task>? AuthenticatedMessageReceived;

    public bool IsListening => _transport.IsListening;
    public PairingPrompt? PendingPairing => _pairing.Pending;
    public IReadOnlyList<PairedDevice> PairedDevices => _pairedStore.ReadAll();
    public PairedDevice? ConnectedDevice
    {
        get { lock (_sessionSync) return _active?.Device; }
    }

    public NativePeerHost(NativeIdentity identity, string dataDirectory, int port = ProtocolV1.Port)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        _hostId = identity.Id;
        _hostName = identity.Name;
        _pairedStore = new JsonPairedDeviceStore(Path.Combine(dataDirectory, "paired.json"));
        _pairing = new PairingResponder(identity.Id, identity.Name, identity.Fingerprint, _pairedStore);
        _transport = new TlsWebSocketServer(identity.Certificate, port);

        _transport.TextReceived += HandleTextAsync;
        _transport.ConnectionClosed += HandleConnectionClosed;
        _pairing.MessageReady += SendPairingMessage;
        _pairing.PromptChanged += _ => StateChanged?.Invoke();
        _pairing.CloseConnectionRequested += CloseConnection;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
        _heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _heartbeatTask = HeartbeatLoopAsync(_heartbeatStop.Token);
        StateChanged?.Invoke();
    }

    public void ApprovePairing(bool allow) => _pairing.Approve(allow);

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        PeerSession? active;
        lock (_sessionSync) active = _active;
        if (active is null) return;

        await SendErrorAsync(active.Connection, ProtocolV1.Error.Released, "Disconnected on this PC", cancellationToken)
            .ConfigureAwait(false);
        await active.Connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnected on this PC", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleTextAsync(TlsWebSocketConnection connection, string text)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException)
        {
            await SendErrorAsync(connection, ProtocolV1.Error.BadMessage, "Expected a JSON object with a t field")
                .ConfigureAwait(false);
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                await SendErrorAsync(connection, ProtocolV1.Error.BadMessage, "Expected a JSON object with a t field")
                    .ConfigureAwait(false);
                return;
            }

            var type = typeElement.GetString() ?? string.Empty;
            if (_sessions.TryGetValue(connection.Id, out var session))
            {
                await HandleAuthenticatedMessageAsync(session, type, root).ConfigureAwait(false);
                return;
            }

            switch (type)
            {
                case ProtocolV1.Message.PairStart:
                    _pairing.Begin(connection.Id, connection.RemoteAddress, new PairingStartRequest(
                        GetInt(root, "v"),
                        GetString(root, "id"),
                        GetString(root, "name"),
                        GetString(root, "os"),
                        GetString(root, "commit")));
                    return;

                case ProtocolV1.Message.PairReveal:
                    _pairing.Reveal(connection.Id, GetString(root, "nonce"));
                    return;

                case ProtocolV1.Message.PairConfirm:
                    _pairing.Confirm(connection.Id);
                    return;

                case ProtocolV1.Message.PairCancel:
                    _pairing.Cancel(connection.Id);
                    return;

                case ProtocolV1.Message.Hello:
                    await AuthenticateAsync(connection, root).ConfigureAwait(false);
                    return;

                default:
                    await SendErrorAsync(connection, ProtocolV1.Error.BadMessage,
                        $"Message type \"{type}\" is not allowed before welcome").ConfigureAwait(false);
                    return;
            }
        }
    }

    private async Task AuthenticateAsync(TlsWebSocketConnection connection, JsonElement message)
    {
        var version = GetInt(message, "v");
        if (version != ProtocolV1.Version)
        {
            await SendErrorAsync(connection, ProtocolV1.Error.VersionMismatch,
                $"This PC speaks protocol v{ProtocolV1.Version}").ConfigureAwait(false);
            return;
        }

        var deviceId = GetString(message, "id");
        var token = GetString(message, "token");
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrEmpty(token))
        {
            await SendErrorAsync(connection, ProtocolV1.Error.BadMessage, "hello needs id and token").ConfigureAwait(false);
            return;
        }

        var paired = _pairedStore.ReadAll().FirstOrDefault(x => x.Id == deviceId);
        if (paired is null || !ProtocolCrypto.TokenMatches(token, paired.TokenHash))
        {
            await SendErrorAsync(connection, ProtocolV1.Error.BadToken, "Not paired with this computer").ConfigureAwait(false);
            return;
        }

        var reportedOs = GetString(message, "os");
        var os = PeerPlatformPolicy.TryParseWireId(reportedOs, out var platform)
            ? PeerPlatformPolicy.ToWireId(platform)
            : paired.Os;
        if (!PeerPlatformPolicy.TryParseWireId(os, out platform))
        {
            await SendErrorAsync(connection, ProtocolV1.Error.BadMessage, "Unsupported peer platform").ConfigureAwait(false);
            return;
        }

        var name = GetString(message, "name");
        if (string.IsNullOrWhiteSpace(name)) name = paired.Name;
        name = name[..Math.Min(64, name.Length)];

        var updated = paired with { Name = name, Os = os, LastSeen = DateTimeOffset.UtcNow };
        _pairedStore.Upsert(updated);

        var session = new PeerSession(connection, updated, os);
        PeerSession? replaced = null;
        lock (_sessionSync)
        {
            if (_active is not null && _active.Connection.Id != connection.Id)
            {
                var takeover = os == ProtocolV1.PeerOs.Android || GetBool(message, "takeover");
                if (!takeover)
                {
                    _ = SendErrorAsync(connection, ProtocolV1.Error.Busy,
                        $"In use by {_active.Device.Name}");
                    return;
                }
                replaced = _active;
            }

            _sessions[connection.Id] = session;
            _active = session;
        }

        if (replaced is not null)
        {
            _sessions.TryRemove(replaced.Connection.Id, out _);
            await SendErrorAsync(replaced.Connection, ProtocolV1.Error.Replaced,
                $"{name} took over").ConfigureAwait(false);
            await replaced.Connection.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                "Session replaced by another peer").ConfigureAwait(false);
        }

        await connection.SendTextAsync(JsonSerializer.Serialize(new
        {
            t = ProtocolV1.Message.Welcome,
            v = ProtocolV1.Version,
            macId = _hostId,
            macName = _hostName,
            os = ProtocolV1.PeerOs.Windows
        })).ConfigureAwait(false);

        StateChanged?.Invoke();
    }

    private async Task HandleAuthenticatedMessageAsync(PeerSession session, string type, JsonElement message)
    {
        session.Touch();

        switch (type)
        {
            case ProtocolV1.Message.Ping:
                await session.Connection.SendTextAsync(JsonSerializer.Serialize(new
                {
                    t = ProtocolV1.Message.Pong,
                    ts = GetLong(message, "ts")
                })).ConfigureAwait(false);
                return;

            case ProtocolV1.Message.Pong:
                return;

            case ProtocolV1.Message.Bye:
                await session.Connection.CloseAsync(WebSocketCloseStatus.NormalClosure,
                    "Peer disconnected").ConfigureAwait(false);
                return;
        }

        var handler = AuthenticatedMessageReceived;
        if (handler is not null)
            await handler(session.Device, type, message.Clone()).ConfigureAwait(false);
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(ProtocolV1.PingIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                PeerSession? active;
                lock (_sessionSync) active = _active;
                if (active is null) continue;

                if (DateTimeOffset.UtcNow - active.LastReceived > TimeSpan.FromMilliseconds(ProtocolV1.SessionTimeoutMs))
                {
                    await active.Connection.CloseAsync(WebSocketCloseStatus.EndpointUnavailable,
                        "Peer stopped responding", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await active.Connection.SendTextAsync(JsonSerializer.Serialize(new
                {
                    t = ProtocolV1.Message.Ping,
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
    }

    private void SendPairingMessage(PairingWireMessage message)
    {
        var connection = _transport.Connections.FirstOrDefault(candidate => candidate.Id == message.ConnectionId);
        if (connection is not null)
            _ = SendSafelyAsync(connection, message.ToJson());
    }

    private async Task SendSafelyAsync(TlsWebSocketConnection connection, string text)
    {
        try { await connection.SendTextAsync(text).ConfigureAwait(false); }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
    }

    private void CloseConnection(string connectionId)
    {
        var connection = _transport.Connections.FirstOrDefault(candidate => candidate.Id == connectionId);
        if (connection is not null)
            _ = CloseSafelyAsync(connection);
    }

    private static async Task CloseSafelyAsync(TlsWebSocketConnection connection)
    {
        try
        {
            await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "Pairing cancelled")
                .ConfigureAwait(false);
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
    }

    private void HandleConnectionClosed(TlsWebSocketConnection connection)
    {
        _pairing.ConnectionClosed(connection.Id);
        _sessions.TryRemove(connection.Id, out _);
        lock (_sessionSync)
        {
            if (_active?.Connection.Id == connection.Id)
                _active = null;
        }
        StateChanged?.Invoke();
    }

    private static Task SendErrorAsync(TlsWebSocketConnection connection, string code, string message,
        CancellationToken cancellationToken = default) =>
        connection.SendTextAsync(JsonSerializer.Serialize(new
        {
            t = ProtocolV1.Message.Error,
            code,
            message
        }), cancellationToken);

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static long GetLong(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var result) ? result : 0;

    private static bool GetBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        var stop = _heartbeatStop;
        _heartbeatStop = null;
        if (stop is not null)
            await stop.CancelAsync().ConfigureAwait(false);

        if (_heartbeatTask is not null)
        {
            try { await _heartbeatTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        await _pairing.DisposeAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        stop?.Dispose();

        lock (_sessionSync) _active = null;
        _sessions.Clear();
        StateChanged = null;
        AuthenticatedMessageReceived = null;
    }

    private sealed class PeerSession(TlsWebSocketConnection connection, PairedDevice device, string os)
    {
        private long _lastReceivedTicks = DateTime.UtcNow.Ticks;
        public TlsWebSocketConnection Connection { get; } = connection;
        public PairedDevice Device { get; } = device;
        public string Os { get; } = os;
        public DateTimeOffset LastReceived => new(Interlocked.Read(ref _lastReceivedTicks), TimeSpan.Zero);
        public void Touch() => Interlocked.Exchange(ref _lastReceivedTicks, DateTime.UtcNow.Ticks);
    }
}
