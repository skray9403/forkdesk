using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TwinDesk.Native.Core;

public enum FileTransferState
{
    Pending,
    Transferring,
    Completed,
    Failed,
    Canceled
}

public enum FileTransferDirection
{
    Send,
    Receive
}

public sealed record FileTransferItem(
    string Id,
    string FileName,
    long TotalBytes,
    long TransferredBytes,
    FileTransferDirection Direction,
    FileTransferState State,
    string StatusMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt = null,
    string? LocalPath = null)
{
    public double ProgressPercent => TotalBytes <= 0 ? 0 : Math.Clamp((double)TransferredBytes / TotalBytes * 100.0, 0, 100.0);
}

public sealed record FileTransferOptions(bool Enabled = true, string? DownloadDirectory = null);

public sealed record FileTransferSnapshot(
    FileTransferOptions Options,
    bool IsConnected,
    string Status,
    IReadOnlyList<FileTransferItem> ActiveTransfers,
    IReadOnlyList<FileTransferItem> RecentTransfers);

public sealed class FileTransferService : IDisposable
{
    private sealed class ActiveTransfer
    {
        public required string Id { get; init; }
        public required string FileName { get; init; }
        public required long TotalBytes { get; init; }
        public long TransferredBytes { get; set; }
        public required FileTransferDirection Direction { get; init; }
        public FileTransferState State { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedAt { get; set; }
        public string? LocalPath { get; set; }
        public string? TempPath { get; set; }
        public int NextSeq { get; set; }
        public IncrementalHash? Hasher { get; set; }
        public FileStream? Stream { get; set; }
        public CancellationTokenSource? Cts { get; set; }

        public FileTransferItem ToItem() => new(
            Id, FileName, TotalBytes, TransferredBytes, Direction, State,
            StatusMessage, StartedAt, CompletedAt, LocalPath);
    }

    private readonly object _sync = new();
    private readonly string _settingsPath;
    private readonly string _deviceId;
    private readonly Func<string, CancellationToken, Task<bool>> _sendMessage;
    private readonly ConcurrentDictionary<string, ActiveTransfer> _transfers = new(StringComparer.Ordinal);
    private readonly List<FileTransferItem> _history = new();
    private FileTransferOptions _options;
    private bool _isConnected;
    private string _status = "File transfer is ready";
    private bool _disposed;

    public event EventHandler<FileTransferSnapshot>? SnapshotChanged;

    public string DefaultDownloadDirectory { get; }

    public FileTransferService(string dataDirectory, string deviceId, Func<string, CancellationToken, Task<bool>> sendMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        _sendMessage = sendMessage ?? throw new ArgumentNullException(nameof(sendMessage));
        _deviceId = deviceId;

        DefaultDownloadDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "TwinDesk");

        Directory.CreateDirectory(dataDirectory);
        _settingsPath = Path.Combine(dataDirectory, "files.json");
        _options = LoadOptions(_settingsPath, DefaultDownloadDirectory);
    }

    public FileTransferSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                var active = _transfers.Values
                    .Where(t => t.State is FileTransferState.Pending or FileTransferState.Transferring)
                    .OrderByDescending(t => t.StartedAt)
                    .Select(t => t.ToItem())
                    .ToList();

                var recent = _history
                    .OrderByDescending(t => t.CompletedAt ?? t.StartedAt)
                    .Take(25)
                    .ToList();

                return new FileTransferSnapshot(_options, _isConnected, _status, active, recent);
            }
        }
    }

    public Task SetOptionsAsync(FileTransferOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            var downloadDir = string.IsNullOrWhiteSpace(options.DownloadDirectory)
                ? DefaultDownloadDirectory
                : options.DownloadDirectory;

            _options = options with { DownloadDirectory = downloadDir };
            _status = _options.Enabled ? "File transfer is enabled" : "File transfer is disabled";
        }

        SaveOptions(_settingsPath, _options);
        Publish();
        return Task.CompletedTask;
    }

    public Task SetSessionActiveAsync(bool connected, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_isConnected == connected) return Task.CompletedTask;
            _isConnected = connected;
            if (!connected)
            {
                // Abort in-flight transfers if peer disconnected
                foreach (var transfer in _transfers.Values.Where(t => t.State is FileTransferState.Pending or FileTransferState.Transferring))
                {
                    transfer.State = FileTransferState.Failed;
                    transfer.StatusMessage = "Peer disconnected";
                    transfer.CompletedAt = DateTimeOffset.UtcNow;
                    transfer.Cts?.Cancel();
                    CleanupTransferStream(transfer);
                    _history.Add(transfer.ToItem());
                }
                _status = "Disconnected from peer";
            }
            else
            {
                _status = "Connected to peer";
            }
        }
        Publish();
        return Task.CompletedTask;
    }

    public async Task<string> StartSendFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found.", filePath);

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length > ProtocolV1.FileMaxBytes)
            throw new InvalidOperationException($"File exceeds maximum supported size ({ProtocolV1.FileMaxBytes / (1024L * 1024 * 1024 * 1024)} TiB).");

        lock (_sync)
        {
            if (!_isConnected)
                throw new InvalidOperationException("No peer is currently connected.");
            if (!_options.Enabled)
                throw new InvalidOperationException("File transfer is disabled.");
        }

        var id = Guid.NewGuid().ToString("N");
        var fileName = Path.GetFileName(filePath);
        var transfer = new ActiveTransfer
        {
            Id = id,
            FileName = fileName,
            TotalBytes = fileInfo.Length,
            Direction = FileTransferDirection.Send,
            State = FileTransferState.Pending,
            StatusMessage = "Offering file to peer…",
            LocalPath = filePath,
            Cts = new CancellationTokenSource()
        };

        _transfers[id] = transfer;
        Publish("Offering file to peer…");

        var payload = JsonSerializer.Serialize(new
        {
            t = ProtocolV1.Message.FileOffer,
            id,
            name = fileName,
            size = fileInfo.Length,
            index = 0,
            count = 1
        });

        if (!await _sendMessage(payload, cancellationToken).ConfigureAwait(false))
        {
            transfer.State = FileTransferState.Failed;
            transfer.StatusMessage = "Failed to send file offer to peer.";
            transfer.CompletedAt = DateTimeOffset.UtcNow;
            _history.Add(transfer.ToItem());
            Publish("Failed to send file offer");
            throw new IOException("Failed to send file offer over peer session.");
        }

        return id;
    }

    public async Task CancelTransferAsync(string id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_transfers.TryGetValue(id, out var transfer)) return;

        transfer.Cts?.Cancel();
        transfer.State = FileTransferState.Canceled;
        transfer.StatusMessage = "Transfer canceled";
        transfer.CompletedAt = DateTimeOffset.UtcNow;
        CleanupTransferStream(transfer);

        lock (_sync)
        {
            _history.Add(transfer.ToItem());
        }

        var payload = JsonSerializer.Serialize(new { t = ProtocolV1.Message.FileCancel, id });
        await _sendMessage(payload, cancellationToken).ConfigureAwait(false);
        Publish("File transfer canceled");
    }

    public async Task HandlePeerMessageAsync(string type, JsonElement message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        switch (type)
        {
            case ProtocolV1.Message.FileOffer:
                await HandleFileOfferAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case ProtocolV1.Message.FileAccept:
                await HandleFileAcceptAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case ProtocolV1.Message.FileRefuse:
                HandleFileRefuse(message);
                break;

            case ProtocolV1.Message.FileChunk:
                await HandleFileChunkAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case ProtocolV1.Message.FileEnd:
                await HandleFileEndAsync(message, cancellationToken).ConfigureAwait(false);
                break;

            case ProtocolV1.Message.FileDone:
                HandleFileDone(message);
                break;

            case ProtocolV1.Message.FileCancel:
                HandleFileCancel(message);
                break;
        }
    }

    private async Task HandleFileOfferAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (!message.TryGetProperty("id", out var idElem) || idElem.GetString() is not { Length: > 0 } id) return;
        var name = message.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? "file" : "file";
        var size = message.TryGetProperty("size", out var sizeElem) && sizeElem.TryGetInt64(out var s) ? s : 0;

        bool enabled;
        string downloadDir;
        lock (_sync)
        {
            enabled = _options.Enabled;
            downloadDir = _options.DownloadDirectory ?? DefaultDownloadDirectory;
        }

        if (!enabled)
        {
            await _sendMessage(JsonSerializer.Serialize(new { t = ProtocolV1.Message.FileRefuse, id, why = "disabled" }), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (size < 0 || size > ProtocolV1.FileMaxBytes)
        {
            await _sendMessage(JsonSerializer.Serialize(new { t = ProtocolV1.Message.FileRefuse, id, why = "too_big" }), cancellationToken).ConfigureAwait(false);
            return;
        }

        var sanitizedName = SanitizeFileName(name);
        var tempDir = Path.Combine(downloadDir, ".tmp");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, $"{id}.part");

        var transfer = new ActiveTransfer
        {
            Id = id,
            FileName = sanitizedName,
            TotalBytes = size,
            Direction = FileTransferDirection.Receive,
            State = FileTransferState.Transferring,
            StatusMessage = "Receiving incoming file…",
            TempPath = tempPath,
            Hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            Stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None),
            Cts = new CancellationTokenSource()
        };

        _transfers[id] = transfer;
        Publish($"Receiving {sanitizedName}…");

        await _sendMessage(JsonSerializer.Serialize(new { t = ProtocolV1.Message.FileAccept, id }), cancellationToken).ConfigureAwait(false);
    }

    private Task HandleFileAcceptAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (!message.TryGetProperty("id", out var idElem) || idElem.GetString() is not { Length: > 0 } id)
            return Task.CompletedTask;

        if (!_transfers.TryGetValue(id, out var transfer) || transfer.Direction != FileTransferDirection.Send || transfer.LocalPath is null)
            return Task.CompletedTask;

        transfer.State = FileTransferState.Transferring;
        transfer.StatusMessage = "Sending data…";
        Publish($"Sending {transfer.FileName}…");

        // Stream file chunks in the background
        _ = Task.Run(() => StreamFileChunksAsync(transfer), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task StreamFileChunksAsync(ActiveTransfer transfer)
    {
        var token = transfer.Cts?.Token ?? CancellationToken.None;
        try
        {
            using var fileStream = new FileStream(transfer.LocalPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[ProtocolV1.FileChunkBytes];
            int bytesRead;
            var seq = 0;

            while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
            {
                token.ThrowIfCancellationRequested();
                hasher.AppendData(buffer, 0, bytesRead);
                var base64 = Convert.ToBase64String(buffer, 0, bytesRead);

                var chunkPayload = JsonSerializer.Serialize(new
                {
                    t = ProtocolV1.Message.FileChunk,
                    id = transfer.Id,
                    seq,
                    data = base64
                });

                if (!await _sendMessage(chunkPayload, token).ConfigureAwait(false))
                {
                    throw new IOException("Failed to deliver file chunk to peer.");
                }

                transfer.TransferredBytes += bytesRead;
                seq++;
                Publish();
            }

            var hash = hasher.GetHashAndReset();
            var hashHex = Convert.ToHexString(hash).ToLowerInvariant();

            var endPayload = JsonSerializer.Serialize(new
            {
                t = ProtocolV1.Message.FileEnd,
                id = transfer.Id,
                sha256 = hashHex
            });

            await _sendMessage(endPayload, token).ConfigureAwait(false);
            transfer.StatusMessage = "Verifying transfer with peer…";
            Publish();
        }
        catch (OperationCanceledException)
        {
            transfer.State = FileTransferState.Canceled;
            transfer.StatusMessage = "Canceled";
            transfer.CompletedAt = DateTimeOffset.UtcNow;
            lock (_sync) _history.Add(transfer.ToItem());
            Publish();
        }
        catch (Exception ex)
        {
            transfer.State = FileTransferState.Failed;
            transfer.StatusMessage = $"Send failed: {ex.Message}";
            transfer.CompletedAt = DateTimeOffset.UtcNow;
            lock (_sync) _history.Add(transfer.ToItem());
            Publish();
        }
    }

    private void HandleFileRefuse(JsonElement message)
    {
        if (!message.TryGetProperty("id", out var idElem) || idElem.GetString() is not { Length: > 0 } id) return;
        if (!_transfers.TryGetValue(id, out var transfer)) return;

        var why = message.TryGetProperty("why", out var whyElem) ? whyElem.GetString() : "Peer refused";
        transfer.State = FileTransferState.Failed;
        transfer.StatusMessage = $"Refused by peer: {why}";
        transfer.CompletedAt = DateTimeOffset.UtcNow;
        CleanupTransferStream(transfer);

        lock (_sync) _history.Add(transfer.ToItem());
        Publish($"File refused: {why}");
    }

    private async Task HandleFileChunkAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (!message.TryGetProperty("id", out var idElem) || idElem.GetString() is not { Length: > 0 } id) return;
        if (!_transfers.TryGetValue(id, out var transfer) || transfer.Direction != FileTransferDirection.Receive || transfer.Stream is null) return;

        var seq = message.TryGetProperty("seq", out var seqElem) && seqElem.TryGetInt32(out var s) ? s : -1;
        if (seq != transfer.NextSeq)
        {
            transfer.State = FileTransferState.Failed;
            transfer.StatusMessage = $"Out-of-order chunk: expected {transfer.NextSeq}, got {seq}";
            transfer.CompletedAt = DateTimeOffset.UtcNow;
            CleanupTransferStream(transfer);
            lock (_sync) _history.Add(transfer.ToItem());
            await _sendMessage(JsonSerializer.Serialize(new { t = ProtocolV1.Message.FileCancel, id }), cancellationToken).ConfigureAwait(false);
            Publish("Chunk sequence error");
            return;
        }

        if (!message.TryGetProperty("data", out var dataElem) || dataElem.GetString() is not { Length: > 0 } base64) return;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch
        {
            transfer.State = FileTransferState.Failed;
            transfer.StatusMessage = "Invalid base64 payload";
            transfer.CompletedAt = DateTimeOffset.UtcNow;
            CleanupTransferStream(transfer);
            lock (_sync) _history.Add(transfer.ToItem());
            await _sendMessage(JsonSerializer.Serialize(new { t = ProtocolV1.Message.FileCancel, id }), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (transfer.TransferredBytes + bytes.Length > transfer.TotalBytes)
        {
            transfer.State = FileTransferState.Failed;
            transfer.StatusMessage = "File size overflow";
            transfer.CompletedAt = DateTimeOffset.UtcNow;
            CleanupTransferStream(transfer);
            lock (_sync) _history.Add(transfer.ToItem());
            await _sendMessage(JsonSerializer.Serialize(new { t = ProtocolV1.Message.FileCancel, id }), cancellationToken).ConfigureAwait(false);
            return;
        }

        await transfer.Stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        transfer.Hasher?.AppendData(bytes);
        transfer.TransferredBytes += bytes.Length;
        transfer.NextSeq++;
        Publish();
    }

    private async Task HandleFileEndAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (!message.TryGetProperty("id", out var idElem) || idElem.GetString() is not { Length: > 0 } id) return;
        if (!_transfers.TryGetValue(id, out var transfer) || transfer.Direction != FileTransferDirection.Receive) return;

        var remoteHash = message.TryGetProperty("sha256", out var hashElem) ? hashElem.GetString() : string.Empty;

        // Flush and close stream
        if (transfer.Stream is not null)
        {
            await transfer.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            transfer.Stream.Dispose();
            transfer.Stream = null;
        }

        var localHash = transfer.Hasher is not null
            ? Convert.ToHexString(transfer.Hasher.GetHashAndReset()).ToLowerInvariant()
            : string.Empty;

        bool ok = string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase) && transfer.TransferredBytes == transfer.TotalBytes;

        string? finalPath = null;
        if (ok && transfer.TempPath is not null && File.Exists(transfer.TempPath))
        {
            string downloadDir;
            lock (_sync) downloadDir = _options.DownloadDirectory ?? DefaultDownloadDirectory;
            Directory.CreateDirectory(downloadDir);

            finalPath = GetUniqueDestinationPath(downloadDir, transfer.FileName);
            File.Move(transfer.TempPath, finalPath, overwrite: true);
            transfer.LocalPath = finalPath;
            transfer.State = FileTransferState.Completed;
            transfer.StatusMessage = "Received successfully";
            transfer.CompletedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            if (transfer.TempPath is not null && File.Exists(transfer.TempPath))
            {
                try { File.Delete(transfer.TempPath); } catch { }
            }
            transfer.State = FileTransferState.Failed;
            transfer.StatusMessage = "Checksum mismatch or incomplete file";
            transfer.CompletedAt = DateTimeOffset.UtcNow;
        }

        lock (_sync) _history.Add(transfer.ToItem());

        var replyPayload = JsonSerializer.Serialize(new
        {
            t = ProtocolV1.Message.FileDone,
            id,
            ok,
            path = finalPath,
            error = ok ? null : "checksum mismatch"
        });

        await _sendMessage(replyPayload, cancellationToken).ConfigureAwait(false);
        Publish(ok ? $"Received {transfer.FileName}" : "File verification failed");
    }

    private void HandleFileDone(JsonElement message)
    {
        if (!message.TryGetProperty("id", out var idElem) || idElem.GetString() is not { Length: > 0 } id) return;
        if (!_transfers.TryGetValue(id, out var transfer) || transfer.Direction != FileTransferDirection.Send) return;

        var ok = message.TryGetProperty("ok", out var okElem) && okElem.ValueKind == JsonValueKind.True;
        var error = message.TryGetProperty("error", out var errElem) ? errElem.GetString() : null;

        transfer.State = ok ? FileTransferState.Completed : FileTransferState.Failed;
        transfer.StatusMessage = ok ? "Sent successfully" : (error ?? "Peer reported file transfer failure");
        transfer.CompletedAt = DateTimeOffset.UtcNow;
        CleanupTransferStream(transfer);

        lock (_sync) _history.Add(transfer.ToItem());
        Publish(ok ? $"Sent {transfer.FileName}" : $"Send failed: {transfer.StatusMessage}");
    }

    private void HandleFileCancel(JsonElement message)
    {
        if (!message.TryGetProperty("id", out var idElem) || idElem.GetString() is not { Length: > 0 } id) return;
        if (!_transfers.TryGetValue(id, out var transfer)) return;

        transfer.Cts?.Cancel();
        transfer.State = FileTransferState.Canceled;
        transfer.StatusMessage = "Peer canceled transfer";
        transfer.CompletedAt = DateTimeOffset.UtcNow;
        CleanupTransferStream(transfer);

        lock (_sync) _history.Add(transfer.ToItem());
        Publish("Peer canceled file transfer");
    }

    private static void CleanupTransferStream(ActiveTransfer transfer)
    {
        if (transfer.Stream is not null)
        {
            try { transfer.Stream.Dispose(); } catch { }
            transfer.Stream = null;
        }
        if (transfer.Hasher is not null)
        {
            try { transfer.Hasher.Dispose(); } catch { }
            transfer.Hasher = null;
        }
        if (transfer.TempPath is not null && File.Exists(transfer.TempPath) && transfer.State != FileTransferState.Completed)
        {
            try { File.Delete(transfer.TempPath); } catch { }
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(fileName.Where(c => !invalid.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "downloaded_file" : clean;
    }

    private static string GetUniqueDestinationPath(string directory, string fileName)
    {
        var target = Path.Combine(directory, fileName);
        if (!File.Exists(target)) return target;

        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var index = 1;

        while (true)
        {
            var candidate = Path.Combine(directory, $"{nameWithoutExt} ({index}){ext}");
            if (!File.Exists(candidate)) return candidate;
            index++;
        }
    }

    private void Publish(string? status = null)
    {
        if (!string.IsNullOrWhiteSpace(status))
            _status = status;
        SnapshotChanged?.Invoke(this, Snapshot);
    }

    private static FileTransferOptions LoadOptions(string path, string defaultDownloadDir)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var options = JsonSerializer.Deserialize<FileTransferOptions>(json);
                if (options is not null)
                {
                    return options with
                    {
                        DownloadDirectory = string.IsNullOrWhiteSpace(options.DownloadDirectory)
                            ? defaultDownloadDir
                            : options.DownloadDirectory
                    };
                }
            }
        }
        catch { }

        return new FileTransferOptions(Enabled: true, DownloadDirectory: defaultDownloadDir);
    }

    private static void SaveOptions(string path, FileTransferOptions options)
    {
        try
        {
            var json = JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var transfer in _transfers.Values)
        {
            transfer.Cts?.Cancel();
            CleanupTransferStream(transfer);
        }
        _transfers.Clear();
    }
}
