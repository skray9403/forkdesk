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
    private DateTimeOffset _phoneGraceUntil = DateTimeOffset.MinValue;
    private string? _phoneGraceDeviceId;
    private string? _phoneGraceDeviceName;
    private readonly TimeSpan _phoneGraceDuration;
    private bool _disposed;

    public event Action? StateChanged;
    public event Action<Exception>? TransportError;
    public event Func<PairedDevice, string, JsonElement, Task>? AuthenticatedMessageReceived;

    public bool IsListening => _transport.IsListening;
    public int Port => _transport.Port;
    public PairingPrompt? PendingPairing => _pairing.Pending;
    public IReadOnlyList<PairedDevice> PairedDevices => _pairedStore.ReadAll();
    public PairedDevice? ConnectedDevice
    {
        get { lock (_sessionSync) return _active?.Device; }
    }

    public bool IsPhoneGraceActive
    {
        get { lock (_sessionSync) return IsPhoneGraceActiveLocked(DateTimeOffset.UtcNow); }
    }

    public string? PhoneGraceDeviceId
    {
        get { lock (_sessionSync) return IsPhoneGraceActiveLocked(DateTimeOffset.UtcNow) ? _phoneGraceDeviceId : null; }
    }

    public NativePeerHost(
        NativeIdentity identity,
        string dataDirectory,
        int port = ProtocolV1.Port,
        TimeSpan? phoneGraceDuration = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        _hostId = identity.Id;
        _hostName = identity.Name;
        _phoneGraceDuration = phoneGraceDuration ?? TimeSpan.FromMilliseconds(ProtocolV1.PhoneGraceMs);
        if (_phoneGraceDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(phoneGraceDuration));
        _pairedStore = new JsonPairedDeviceStore(Path.Combine(dataDirectory, "paired.json"));
        _pairing = new PairingResponder(identity.Id, identity.Name, identity.Fingerprint, _pairedStore);
        _transport = new TlsWebSocketServer(identity.Certificate, port);

        _transport.TextReceived += HandleTextAsync;
        _transport.ConnectionClosed += HandleConnectionClosed;
        _transport.ConnectionError += ex => TransportError?.Invoke(ex);
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

    public async Task<bool> ForgetDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var removed = _pairing.ForgetDevice(deviceId);
        PeerSession? active;
        lock (_sessionSync)
            active = _active?.Device.Id == deviceId ? _active : null;

        if (active is not null)
        {
            await EndSessionAsync(
                active,
                WebSocketCloseStatus.PolicyViolation,
                "This computer forgot the pairing",
                ProtocolV1.Error.BadToken).ConfigureAwait(false);
            removed = true;
        }

        StateChanged?.Invoke();
        return removed;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        PeerSession? active;
        TlsWebSocketConnection[] connections;
        lock (_sessionSync)
        {
            active = _active;
            if (active is null) return;
            connections = active.GetConnections();
            active.Released = true;
            RemoveSessionMappingsLocked(active);
            _active = null;
        }

        foreach (var connection in connections)
        {
            try
            {
                await SendErrorAsync(connection, ProtocolV1.Error.Released,
                    "Disconnected on this PC", cancellationToken).ConfigureAwait(false);
                await connection.CloseAsync(WebSocketCloseStatus.NormalClosure,
                    "Disconnected on this PC", cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
        }
        StateChanged?.Invoke();
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
                await HandleAuthenticatedMessageAsync(session, connection, type, root).ConfigureAwait(false);
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
                    if (root.TryGetProperty("resume", out var resumeElement) &&
                        resumeElement.ValueKind == JsonValueKind.String)
                        await ResumeAsync(connection, root).ConfigureAwait(false);
                    else
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
        TlsWebSocketConnection[] replacedConnections = [];
        string? busyName = null;
        lock (_sessionSync)
        {
            var explicitTakeover = GetBool(message, "takeover");
            if (os == ProtocolV1.PeerOs.Android || explicitTakeover)
            {
                ClearPhoneGraceLocked();
            }
            else if (IsPhoneGraceActiveLocked(DateTimeOffset.UtcNow))
            {
                busyName = _phoneGraceDeviceName ?? "phone";
            }

            if (busyName is null && _active is not null && _active.PrimaryConnection.Id != connection.Id)
            {
                var sameDevice = string.Equals(_active.Device.Id, deviceId, StringComparison.Ordinal);
                var takeover = sameDevice || os == ProtocolV1.PeerOs.Android || explicitTakeover;
                if (!takeover)
                    busyName = _active.Device.Name;
                else
                {
                    replaced = _active;
                    replacedConnections = replaced.GetConnections();
                    replaced.Released = true;
                    RemoveSessionMappingsLocked(replaced);
                }
            }

            if (busyName is null)
            {
                _sessions[connection.Id] = session;
                _active = session;
                connection.MarkAuthenticated();
            }
        }

        if (busyName is not null)
        {
            await SendErrorAsync(connection, ProtocolV1.Error.Busy, $"In use by {busyName}").ConfigureAwait(false);
            return;
        }

        if (replaced is not null)
        {
            var code = os == ProtocolV1.PeerOs.Android && replaced.Os != ProtocolV1.PeerOs.Android
                ? ProtocolV1.Error.Busy
                : ProtocolV1.Error.Replaced;
            var reason = code == ProtocolV1.Error.Busy ? $"In use by {name}" : $"{name} took over";

            foreach (var oldConnection in replacedConnections)
            {
                try
                {
                    await SendErrorAsync(oldConnection, code, reason).ConfigureAwait(false);
                    await oldConnection.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason)
                        .ConfigureAwait(false);
                }
                catch (WebSocketException) { }
                catch (ObjectDisposedException) { }
            }
        }

        await connection.SendTextAsync(JsonSerializer.Serialize(new
        {
            t = ProtocolV1.Message.Welcome,
            v = ProtocolV1.Version,
            macId = _hostId,
            macName = _hostName,
            os = ProtocolV1.PeerOs.Windows,
            resume = session.ResumeKey
        })).ConfigureAwait(false);

        StateChanged?.Invoke();
    }

    /// <summary>Moves an authenticated peer to a new socket and keeps the old socket as fallback.</summary>
    private async Task ResumeAsync(TlsWebSocketConnection connection, JsonElement message)
    {
        if (GetInt(message, "v") != ProtocolV1.Version)
        {
            await SendErrorAsync(connection, ProtocolV1.Error.VersionMismatch,
                $"This PC speaks protocol v{ProtocolV1.Version}").ConfigureAwait(false);
            return;
        }

        var deviceId = GetString(message, "id");
        var resumeKey = GetString(message, "resume");
        PeerSession? session;
        TlsWebSocketConnection? supersededPrimary = null;

        lock (_sessionSync)
        {
            session = _active;
            if (session is null ||
                !string.Equals(session.Device.Id, deviceId, StringComparison.Ordinal) ||
                !string.Equals(session.ResumeKey, resumeKey, StringComparison.Ordinal) ||
                string.IsNullOrEmpty(resumeKey))
            {
                session = null;
            }
            else
            {
                if (session.AlternateConnection is null)
                    session.AlternateConnection = session.PrimaryConnection;
                else
                {
                    supersededPrimary = session.PrimaryConnection;
                    _sessions.TryRemove(supersededPrimary.Id, out _);
                }

                session.PrimaryConnection = connection;
                session.Touch();
                connection.MarkAuthenticated();
                _sessions[connection.Id] = session;
            }
        }

        if (session is null)
        {
            await SendErrorAsync(connection, ProtocolV1.Error.BadMessage, "Nothing to resume").ConfigureAwait(false);
            return;
        }

        if (supersededPrimary is not null)
        {
            try
            {
                await supersededPrimary.CloseAsync(WebSocketCloseStatus.NormalClosure,
                    "Session moved to a newer transport").ConfigureAwait(false);
            }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
        }

        await connection.SendTextAsync(JsonSerializer.Serialize(new
        {
            t = ProtocolV1.Message.Welcome,
            v = ProtocolV1.Version,
            resumed = true
        })).ConfigureAwait(false);
        StateChanged?.Invoke();
    }

    private void RemoveSessionMappingsLocked(PeerSession session)
    {
        foreach (var connection in session.GetConnections())
            _sessions.TryRemove(connection.Id, out _);
    }

    private async Task EndSessionAsync(
        PeerSession session,
        WebSocketCloseStatus status,
        string reason,
        string? errorCode = null)
    {
        TlsWebSocketConnection[] connections;
        lock (_sessionSync)
        {
            connections = session.GetConnections();
            session.Released = true;
            RemoveSessionMappingsLocked(session);
            if (_active == session) _active = null;
        }

        foreach (var connection in connections)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(errorCode))
                    await SendErrorAsync(connection, errorCode, reason).ConfigureAwait(false);
                await connection.CloseAsync(status, reason).ConfigureAwait(false);
            }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
        }
        StateChanged?.Invoke();
    }

    private async Task HandleAuthenticatedMessageAsync(
        PeerSession session,
        TlsWebSocketConnection connection,
        string type,
        JsonElement message)
    {
        session.Touch();

        switch (type)
        {
            case ProtocolV1.Message.Ping:
                await connection.SendTextAsync(JsonSerializer.Serialize(new
                {
                    t = ProtocolV1.Message.Pong,
                    ts = GetLong(message, "ts")
                })).ConfigureAwait(false);
                return;

            case ProtocolV1.Message.Pong:
                return;

            case ProtocolV1.Message.Bye:
                await EndSessionAsync(session, WebSocketCloseStatus.NormalClosure, "Peer disconnected")
                    .ConfigureAwait(false);
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
                TlsWebSocketConnection[] connections;
                lock (_sessionSync)
                {
                    active = _active;
                    connections = active?.GetConnections() ?? [];
                }
                if (active is null) continue;

                if (DateTimeOffset.UtcNow - active.LastReceived > TimeSpan.FromMilliseconds(ProtocolV1.SessionTimeoutMs))
                {
                    foreach (var connection in connections)
                    {
                        try
                        {
                            await connection.CloseAsync(WebSocketCloseStatus.EndpointUnavailable,
                                "Peer stopped responding", cancellationToken).ConfigureAwait(false);
                        }
                        catch (WebSocketException) { }
                        catch (ObjectDisposedException) { }
                    }
                    continue;
                }

                var ping = JsonSerializer.Serialize(new
                {
                    t = ProtocolV1.Message.Ping,
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
                foreach (var connection in connections)
                {
                    if (connection.State != WebSocketState.Open) continue;
                    await connection.SendTextAsync(ping, cancellationToken).ConfigureAwait(false);
                }
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
        PeerSession? endedSession = null;

        lock (_sessionSync)
        {
            if (!_sessions.TryRemove(connection.Id, out var session))
                return;

            if (session.AlternateConnection?.Id == connection.Id)
            {
                session.AlternateConnection = null;
            }
            else if (session.PrimaryConnection.Id == connection.Id)
            {
                var alternate = session.AlternateConnection;
                if (alternate is not null && alternate.State == WebSocketState.Open)
                {
                    session.PrimaryConnection = alternate;
                    session.AlternateConnection = null;
                }
                else
                {
                    if (alternate is not null)
                        _sessions.TryRemove(alternate.Id, out _);
                    session.AlternateConnection = null;
                    if (_active == session)
                        _active = null;
                    endedSession = session;
                }
            }
        }

        if (endedSession is not null)
        {
            var now = DateTimeOffset.UtcNow;
            _pairedStore.Upsert(endedSession.Device with { LastSeen = now });

            if (!_disposed && !endedSession.Released &&
                endedSession.Os == ProtocolV1.PeerOs.Android &&
                _phoneGraceDuration > TimeSpan.Zero)
            {
                lock (_sessionSync)
                {
                    _phoneGraceDeviceId = endedSession.Device.Id;
                    _phoneGraceDeviceName = endedSession.Device.Name;
                    _phoneGraceUntil = now + _phoneGraceDuration;
                }
            }
        }

        StateChanged?.Invoke();
    }

    private bool IsPhoneGraceActiveLocked(DateTimeOffset now)
    {
        if (_phoneGraceUntil <= now)
        {
            ClearPhoneGraceLocked();
            return false;
        }

        return _phoneGraceDeviceId is not null;
    }

    private void ClearPhoneGraceLocked()
    {
        _phoneGraceUntil = DateTimeOffset.MinValue;
        _phoneGraceDeviceId = null;
        _phoneGraceDeviceName = null;
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
        TransportError = null;
        AuthenticatedMessageReceived = null;
    }

    private sealed class PeerSession(TlsWebSocketConnection primaryConnection, PairedDevice device, string os)
    {
        private long _lastReceivedTicks = DateTime.UtcNow.Ticks;

        public TlsWebSocketConnection PrimaryConnection { get; set; } = primaryConnection;
        public TlsWebSocketConnection? AlternateConnection { get; set; }
        public PairedDevice Device { get; } = device;
        public string Os { get; } = os;
        public string ResumeKey { get; } = ProtocolCrypto.RandomHex(16);
        public bool Released { get; set; }
        public DateTimeOffset LastReceived => new(Interlocked.Read(ref _lastReceivedTicks), TimeSpan.Zero);

        public TlsWebSocketConnection[] GetConnections() => AlternateConnection is null
            ? [PrimaryConnection]
            : [PrimaryConnection, AlternateConnection];

        public void Touch() => Interlocked.Exchange(ref _lastReceivedTicks, DateTime.UtcNow.Ticks);
    }
}
