using System.Collections.ObjectModel;
using System.Text.Json;

namespace TwinDesk.Native.Core;

public sealed record PairingStartRequest(int Version, string DeviceId, string? DeviceName, string Os, string Commitment);
public sealed record PairingPrompt(string ConnectionId, string DeviceId, string DeviceName, PeerPlatform Platform,
    string ShortAuthenticationCode, bool IsConfirmed, bool IsAllowed, DateTimeOffset ExpiresAt);

public sealed record PairingWireMessage(string ConnectionId, string Type, IReadOnlyDictionary<string, object?> Fields)
{
    public string ToJson()
    {
        var payload = Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        payload["t"] = Type;
        return JsonSerializer.Serialize(payload);
    }
}

public sealed record PairedDevice(string Id, string Name, string Os, string TokenHash,
    DateTimeOffset PairedAt, DateTimeOffset LastSeen);

public interface IPairedDeviceStore
{
    IReadOnlyList<PairedDevice> ReadAll();
    void Upsert(PairedDevice device);
    bool Remove(string deviceId);
}

/// <summary>Atomic JSON store containing token hashes, never reusable pairing tokens.</summary>
public sealed class JsonPairedDeviceStore : IPairedDeviceStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly object _sync = new();

    public JsonPairedDeviceStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public IReadOnlyList<PairedDevice> ReadAll()
    {
        lock (_sync)
        {
            if (!File.Exists(_path)) return Array.Empty<PairedDevice>();
            try
            {
                return JsonSerializer.Deserialize<List<PairedDevice>>(File.ReadAllBytes(_path), Options) ?? [];
            }
            catch (JsonException) { return Array.Empty<PairedDevice>(); }
        }
    }

    public void Upsert(PairedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_sync)
        {
            var next = ReadAll().Where(x => x.Id != device.Id).Append(device).ToArray();
            Write(next);
        }
    }

    public bool Remove(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_sync)
        {
            var old = ReadAll();
            var next = old.Where(x => x.Id != deviceId).ToArray();
            if (next.Length == old.Count) return false;
            Write(next);
            return true;
        }
    }

    private void Write(IReadOnlyList<PairedDevice> devices)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Paired-device path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(devices, Options));
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

/// <summary>
/// Protocol-v1 host-side pairing state machine. It is deliberately transport-independent:
/// the phone must confirm the SAS and the Windows user must separately approve it.
/// </summary>
public sealed class PairingResponder : IAsyncDisposable
{
    public const int MaxFailuresPerAddress = 5;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);

    private readonly object _sync = new();
    private readonly IPairedDeviceStore _store;
    private readonly string _hostId, _hostName, _fingerprint;
    private readonly Func<bool> _isBusy;
    private readonly TimeSpan _timeout;
    private readonly Dictionary<string, List<DateTimeOffset>> _failures = new(StringComparer.Ordinal);
    private PendingState? _pending;
    private bool _disposed;

    public event Action<PairingWireMessage>? MessageReady;
    public event Action<PairingPrompt?>? PromptChanged;
    public event Action<string>? CloseConnectionRequested;

    public PairingResponder(string hostId, string hostName, string hostFingerprint, IPairedDeviceStore store,
        Func<bool>? isBusy = null, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        ArgumentNullException.ThrowIfNull(store);
        _hostId = hostId;
        _hostName = hostName;
        _fingerprint = ProtocolCrypto.NormalizeFingerprint(hostFingerprint);
        if (_fingerprint.Length != 64 || !IsLowerHex(_fingerprint))
            throw new ArgumentException("Host fingerprint must be a SHA-256 hex value.", nameof(hostFingerprint));
        _store = store;
        _isBusy = isBusy ?? (() => false);
        _timeout = timeout ?? TimeSpan.FromMilliseconds(ProtocolV1.PairTimeoutMs);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public PairingPrompt? Pending { get { lock (_sync) return _pending?.ToPrompt(); } }
    public IReadOnlyList<PairedDevice> PairedDevices => _store.ReadAll();

    public bool IsAddressLocked(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        lock (_sync) return LiveFailures(address, DateTimeOffset.UtcNow).Count >= MaxFailuresPerAddress;
    }

    public void Begin(string connectionId, string remoteAddress, PairingStartRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteAddress);
        ArgumentNullException.ThrowIfNull(request);

        lock (_sync)
        {
            ThrowIfDisposed();
            var now = DateTimeOffset.UtcNow;
            if (LiveFailures(remoteAddress, now).Count >= MaxFailuresPerAddress)
            {
                Error(connectionId, ProtocolV1.Error.PairRefused, "Too many failed pairing attempts from this address; wait a few minutes and try again");
                return;
            }
            if (_isBusy())
            {
                Error(connectionId, ProtocolV1.Error.Busy, "Connected to another device");
                return;
            }
            if (_pending is not null)
            {
                Error(connectionId, ProtocolV1.Error.Busy, "Another pairing is in progress");
                return;
            }
            if (request.Version != ProtocolV1.Version)
            {
                Error(connectionId, ProtocolV1.Error.VersionMismatch, $"This PC speaks protocol v{ProtocolV1.Version}");
                return;
            }
            if (string.IsNullOrWhiteSpace(request.DeviceId) || request.DeviceId.Length > 128 ||
                string.IsNullOrWhiteSpace(request.Commitment) || request.Commitment.Length != 64 || !IsLowerHex(request.Commitment))
            {
                FailedStart(remoteAddress, connectionId, "pair.start needs id and a 32-byte commitment");
                return;
            }
            if (!PeerPlatformPolicy.TryParseWireId(request.Os, out var platform))
            {
                FailedStart(remoteAddress, connectionId, "Unsupported peer platform");
                return;
            }

            var nonce = ProtocolCrypto.RandomHex(16);
            var name = string.IsNullOrWhiteSpace(request.DeviceName) ? "PC" : request.DeviceName[..Math.Min(64, request.DeviceName.Length)];
            var pending = new PendingState(connectionId, remoteAddress, request.DeviceId, name, platform,
                request.Commitment, nonce, now + _timeout);
            _pending = pending;
            Send(connectionId, ProtocolV1.Message.PairNonce, new Dictionary<string, object?>
            {
                ["nonce"] = nonce,
                ["os"] = ProtocolV1.PeerOs.Windows
            });
            PromptChanged?.Invoke(pending.ToPrompt());
            _ = ExpireAsync(connectionId, pending.Cancellation.Token);
        }
    }

    public void Reveal(string connectionId, string nonceHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        lock (_sync)
        {
            ThrowIfDisposed();
            var pending = ForConnection(connectionId);
            if (pending is null)
            {
                Error(connectionId, ProtocolV1.Error.BadMessage, "pair.reveal without pair.start");
                return;
            }
            if (pending.WindowsNonce is not null || nonceHex is null || nonceHex.Length != 32 ||
                !IsLowerHex(nonceHex) ||
                !string.Equals(ProtocolCrypto.CommitOf(nonceHex), pending.Commitment, StringComparison.Ordinal))
            {
                Abort(ProtocolV1.Error.PairRefused, "Nonce does not match the commitment");
                return;
            }
            pending.WindowsNonce = nonceHex;
            pending.Code = ProtocolCrypto.SasCode(_fingerprint, nonceHex, pending.HostNonce);
            PromptChanged?.Invoke(pending.ToPrompt());
        }
    }

    public void Confirm(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        lock (_sync)
        {
            ThrowIfDisposed();
            var pending = ForConnection(connectionId);
            if (pending is null)
            {
                Error(connectionId, ProtocolV1.Error.BadMessage, "pair.confirm without pair.start");
                return;
            }
            if (pending.Code is null)
            {
                Abort(ProtocolV1.Error.BadMessage, "pair.confirm before pair.reveal");
                return;
            }
            pending.Confirmed = true;
            PromptChanged?.Invoke(pending.ToPrompt());
            CompleteIfReady();
        }
    }

    public void Approve(bool allow)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            var pending = _pending;
            if (pending is null) return;
            if (!allow)
            {
                Abort(ProtocolV1.Error.PairRefused, "Pairing was denied on this PC");
                return;
            }
            if (pending.Code is null) return;
            pending.Allowed = true;
            PromptChanged?.Invoke(pending.ToPrompt());
            CompleteIfReady();
        }
    }

    public void Cancel(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        lock (_sync)
        {
            ThrowIfDisposed();
            var pending = ForConnection(connectionId);
            if (pending is null) return;
            RecordFailure(pending.RemoteAddress, DateTimeOffset.UtcNow);
            ClearPending();
            CloseConnectionRequested?.Invoke(connectionId);
        }
    }

    public void ConnectionClosed(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return;
        lock (_sync)
        {
            if (_disposed || _pending?.ConnectionId != connectionId) return;
            RecordFailure(_pending.RemoteAddress, DateTimeOffset.UtcNow);
            ClearPending();
        }
    }

    public bool ForgetDevice(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_pending?.DeviceId == deviceId)
            {
                var pending = _pending;
                ClearPending();
                Error(pending.ConnectionId, ProtocolV1.Error.PairRefused,
                    "This computer forgot the pairing");
            }

            return _store.Remove(deviceId);
        }
    }

    private async Task ExpireAsync(string connectionId, CancellationToken cancellationToken)
    {
        try { await Task.Delay(_timeout, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }

        lock (_sync)
        {
            if (_disposed || _pending?.ConnectionId != connectionId) return;
            Abort(ProtocolV1.Error.PairTimeout, "Pairing timed out");
        }
    }

    private void CompleteIfReady()
    {
        var pending = _pending;
        if (pending is null || !pending.Confirmed || !pending.Allowed) return;

        var token = ProtocolCrypto.RandomHex(32);
        var now = DateTimeOffset.UtcNow;
        _store.Upsert(new PairedDevice(pending.DeviceId, pending.DeviceName,
            PeerPlatformPolicy.ToWireId(pending.Platform), ProtocolCrypto.Sha256Hex(token), now, now));
        Send(pending.ConnectionId, ProtocolV1.Message.PairOk, new Dictionary<string, object?>
        {
            ["token"] = token,
            ["macId"] = _hostId,
            ["macName"] = _hostName,
            ["os"] = ProtocolV1.PeerOs.Windows
        });
        _failures.Remove(pending.RemoteAddress);
        ClearPending();
    }

    private void FailedStart(string address, string connectionId, string message)
    {
        RecordFailure(address, DateTimeOffset.UtcNow);
        Error(connectionId, ProtocolV1.Error.BadMessage, message);
    }

    private void Abort(string code, string message)
    {
        var pending = _pending;
        if (pending is null) return;
        RecordFailure(pending.RemoteAddress, DateTimeOffset.UtcNow);
        Error(pending.ConnectionId, code, message);
        ClearPending();
    }

    private void ClearPending()
    {
        var pending = _pending;
        if (pending is null) return;
        _pending = null;
        pending.Cancellation.Cancel();
        pending.Cancellation.Dispose();
        PromptChanged?.Invoke(null);
    }

    private PendingState? ForConnection(string connectionId) =>
        _pending is { } pending && pending.ConnectionId == connectionId ? pending : null;

    private List<DateTimeOffset> LiveFailures(string address, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(address, out var attempts)) return [];
        var live = attempts.Where(at => now - at < FailureWindow).ToList();
        if (live.Count == 0) _failures.Remove(address);
        else _failures[address] = live;
        return live;
    }

    private void RecordFailure(string address, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(address) || address is "127.0.0.1" or "::1") return;
        var live = LiveFailures(address, now);
        live.Add(now);
        _failures[address] = live;
    }

    private void Error(string connectionId, string code, string message) =>
        Send(connectionId, ProtocolV1.Message.Error, new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = message
        });

    private void Send(string connectionId, string type, Dictionary<string, object?> fields) =>
        MessageReady?.Invoke(new PairingWireMessage(connectionId, type,
            new ReadOnlyDictionary<string, object?>(fields)));

    private static bool IsLowerHex(string value) =>
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            ClearPending();
            MessageReady = null;
            PromptChanged = null;
            CloseConnectionRequested = null;
        }
        return ValueTask.CompletedTask;
    }

    private sealed class PendingState(string connectionId, string remoteAddress, string deviceId, string deviceName,
        PeerPlatform platform, string commitment, string hostNonce, DateTimeOffset expiresAt)
    {
        public string ConnectionId { get; } = connectionId;
        public string RemoteAddress { get; } = remoteAddress;
        public string DeviceId { get; } = deviceId;
        public string DeviceName { get; } = deviceName;
        public PeerPlatform Platform { get; } = platform;
        public string Commitment { get; } = commitment;
        public string HostNonce { get; } = hostNonce;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public string? WindowsNonce { get; set; }
        public string? Code { get; set; }
        public bool Confirmed { get; set; }
        public bool Allowed { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();

        public PairingPrompt ToPrompt() => new(ConnectionId, DeviceId, DeviceName, Platform,
            Code ?? string.Empty, Confirmed, Allowed, ExpiresAt);
    }
}
