using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Buffers;

namespace TwinDesk.Native.Core;

/// <summary>
/// A small TLS WebSocket listener for protocol-v1 control traffic.
/// The RFC 6455 upgrade is performed here; frame handling is delegated to
/// System.Net.WebSockets. This avoids an HTTP.sys certificate binding and
/// does not introduce Electron, WebView2 or a browser runtime.
/// </summary>
public sealed class TlsWebSocketServer : IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaxHandshakeBytes = 16 * 1024;
    private const int ReceiveBufferBytes = 64 * 1024;

    private readonly X509Certificate2 _certificate;
    private readonly int _port;
    private readonly ConcurrentDictionary<string, TcpClient> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TlsWebSocketConnection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _handlers = new(StringComparer.Ordinal);
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private Task? _acceptTask;
    private bool _disposed;

    public event Func<TlsWebSocketConnection, string, Task>? TextReceived;
    public event Action<TlsWebSocketConnection>? ConnectionClosed;

    public bool IsListening => _listener is not null;
    public int Port => _port;
    public IReadOnlyCollection<TlsWebSocketConnection> Connections => _connections.Values.ToArray();

    public TlsWebSocketServer(X509Certificate2 certificate, int port = ProtocolV1.Port)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey)
            throw new ArgumentException("The TLS certificate must include its private key.", nameof(certificate));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        _certificate = certificate;
        _port = port;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listener is not null)
            return Task.CompletedTask;

        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start(backlog: 128);
        _listener = listener;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptTask = AcceptLoopAsync(listener, _stop.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }

            client.NoDelay = true;
            var id = Guid.NewGuid().ToString("N");
            _clients[id] = client;
            var handler = HandleClientAsync(id, client, cancellationToken);
            _handlers[id] = handler;
            _ = handler.ContinueWith(
                completed =>
                {
                    _handlers.TryRemove(id, out var ignoredTask);
                    _clients.TryRemove(id, out var ignoredClient);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(string id, TcpClient client, CancellationToken cancellationToken)
    {
        TlsWebSocketConnection? connection = null;
        try
        {
            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, cancellationToken).ConfigureAwait(false);

            var response = await ReadUpgradeRequestAsync(ssl, cancellationToken).ConfigureAwait(false);
            await ssl.WriteAsync(response, cancellationToken).ConfigureAwait(false);
            await ssl.FlushAsync(cancellationToken).ConfigureAwait(false);

            var webSocket = WebSocket.CreateFromStream(
                ssl,
                isServer: true,
                subProtocol: null,
                keepAliveInterval: TimeSpan.FromSeconds(20));

            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            var address = remote?.Address.IsIPv4MappedToIPv6 == true
                ? remote.Address.MapToIPv4().ToString()
                : remote?.Address.ToString() ?? string.Empty;

            connection = new TlsWebSocketConnection(id, address, remote?.Port ?? 0, webSocket);
            _connections[id] = connection;
            await ReceiveLoopAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (WebSocketException) { }
        catch (AuthenticationException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (InvalidDataException) { }
        finally
        {
            if (connection is not null)
            {
                _connections.TryRemove(id, out _);
                await connection.DisposeAsync().ConfigureAwait(false);
                try { ConnectionClosed?.Invoke(connection); } catch { }
            }
            client.Dispose();
        }
    }

    private static async Task<byte[]> ReadUpgradeRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var oneByte = new byte[1];
        var matched = 0;
        byte[] terminator = [13, 10, 13, 10];

        while (buffer.Length < MaxHandshakeBytes)
        {
            var read = await stream.ReadAsync(oneByte.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Peer closed before the WebSocket upgrade.");

            buffer.WriteByte(oneByte[0]);
            matched = oneByte[0] == terminator[matched] ? matched + 1 : oneByte[0] == terminator[0] ? 1 : 0;
            if (matched == terminator.Length)
                break;
        }

        if (buffer.Length >= MaxHandshakeBytes)
            throw new InvalidDataException("WebSocket upgrade headers exceeded the size limit.");

        var headerText = Encoding.ASCII.GetString(buffer.ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var request = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (request.Length != 3 || !request[0].Equals("GET", StringComparison.Ordinal) ||
            !request[2].Equals("HTTP/1.1", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid WebSocket upgrade request line.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) break;
            var separator = line.IndexOf(':');
            if (separator <= 0) throw new InvalidDataException("Invalid WebSocket header.");
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            headers[key] = value;
        }

        if (!headers.TryGetValue("Upgrade", out var upgrade) ||
            !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase) ||
            !headers.TryGetValue("Connection", out var connection) ||
            !connection.Split(',').Any(token => token.Trim().Equals("upgrade", StringComparison.OrdinalIgnoreCase)) ||
            !headers.TryGetValue("Sec-WebSocket-Version", out var version) || version != "13" ||
            !headers.TryGetValue("Sec-WebSocket-Key", out var keyValue))
            throw new InvalidDataException("The request did not contain a valid RFC 6455 WebSocket upgrade.");

        byte[] keyBytes;
        try { keyBytes = Convert.FromBase64String(keyValue); }
        catch (FormatException ex) { throw new InvalidDataException("Invalid WebSocket key.", ex); }
        if (keyBytes.Length != 16)
            throw new InvalidDataException("The WebSocket key must decode to 16 bytes.");

        var acceptSource = Encoding.ASCII.GetBytes(keyValue + WebSocketGuid);
        var accept = Convert.ToBase64String(SHA1.HashData(acceptSource));
        return Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
    }

    private async Task ReceiveLoopAsync(TlsWebSocketConnection connection, CancellationToken cancellationToken)
    {
        var socket = connection.Socket;
        var rented = ArrayPool<byte>.Shared.Rent(ReceiveBufferBytes);
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(rented, 0, ReceiveBufferBytes),
                        cancellationToken).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        await connection.CloseAsync(WebSocketCloseStatus.InvalidMessageType,
                            "Protocol v1 control messages must be text JSON", cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (message.Length + result.Count > ProtocolV1.MaxPayloadBytes)
                    {
                        await connection.CloseAsync(WebSocketCloseStatus.MessageTooBig,
                            "Message exceeds the protocol-v1 payload limit", cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    message.Write(rented, 0, result.Count);
                } while (!result.EndOfMessage);

                string text;
                try { text = new UTF8Encoding(false, true).GetString(message.ToArray()); }
                catch (DecoderFallbackException)
                {
                    await connection.CloseAsync(WebSocketCloseStatus.InvalidPayloadData,
                        "Control message contains invalid UTF-8", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var handler = TextReceived;
                if (handler is not null)
                    await handler(connection, text).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        var stop = _stop;
        _stop = null;
        if (stop is not null)
            await stop.CancelAsync().ConfigureAwait(false);

        _listener?.Stop();
        _listener = null;

        foreach (var client in _clients.Values)
            client.Dispose();

        foreach (var connection in _connections.Values)
            await connection.DisposeAsync().ConfigureAwait(false);

        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        var activeHandlers = _handlers.Values.ToArray();
        if (activeHandlers.Length > 0)
        {
            try { await Task.WhenAll(activeHandlers).ConfigureAwait(false); }
            catch { }
        }

        stop?.Dispose();
        TextReceived = null;
        ConnectionClosed = null;
    }
}

public sealed class TlsWebSocketConnection : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _disposed;

    internal WebSocket Socket { get; }
    public string Id { get; }
    public string RemoteAddress { get; }
    public int RemotePort { get; }
    public WebSocketState State => Socket.State;

    internal TlsWebSocketConnection(string id, string remoteAddress, int remotePort, WebSocket socket)
    {
        Id = id;
        RemoteAddress = remoteAddress;
        RemotePort = remotePort;
        Socket = socket;
    }

    public async Task SendTextAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var bytes = Encoding.UTF8.GetBytes(message);
        if (bytes.Length > ProtocolV1.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(message), "Message exceeds the protocol-v1 payload limit.");

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Socket.State != WebSocketState.Open)
                throw new WebSocketException(WebSocketError.InvalidState, "WebSocket is not open.");

            await Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text,
                endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    public async Task CloseAsync(WebSocketCloseStatus status, string reason,
        CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await Socket.CloseOutputAsync(status, reason, cancellationToken).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await Socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Server stopping",
                    CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
        Socket.Dispose();
        _sendLock.Dispose();
    }
}
