using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TwinDesk.Native.Core;
using Xunit;

namespace TwinDesk.Native.Tests;

public sealed class FileTransferServiceTests : IDisposable
{
    private readonly string _tempDir;

    public FileTransferServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TwinDesk.FileTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task SendFileOffersAndStreamsChunksAcrossMultipleBlocks()
    {
        var sentMessages = new List<string>();
        Task<bool> MockSend(string msg, CancellationToken ct)
        {
            sentMessages.Add(msg);
            return Task.FromResult(true);
        }

        using var service = new FileTransferService(_tempDir, "test-pc", MockSend);
        await service.SetSessionActiveAsync(true);

        // Create 150 KB test file (spans 3 chunks of 64 KB)
        var sourceFile = Path.Combine(_tempDir, "sample_doc.bin");
        var data = new byte[150 * 1024];
        Random.Shared.NextBytes(data);
        await File.WriteAllBytesAsync(sourceFile, data);
        var expectedHash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        var transferId = await service.StartSendFileAsync(sourceFile);
        Assert.False(string.IsNullOrWhiteSpace(transferId));

        // 1. Verify file.offer
        Assert.Single(sentMessages);
        using var offerDoc = JsonDocument.Parse(sentMessages[0]);
        Assert.Equal(ProtocolV1.Message.FileOffer, offerDoc.RootElement.GetProperty("t").GetString());
        Assert.Equal(transferId, offerDoc.RootElement.GetProperty("id").GetString());
        Assert.Equal("sample_doc.bin", offerDoc.RootElement.GetProperty("name").GetString());
        Assert.Equal(data.Length, offerDoc.RootElement.GetProperty("size").GetInt64());

        // 2. Simulate peer file.accept
        var acceptDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            t = ProtocolV1.Message.FileAccept,
            id = transferId
        }));
        await service.HandlePeerMessageAsync(ProtocolV1.Message.FileAccept, acceptDoc.RootElement);

        // Wait for background chunk streaming
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (sentMessages.Count < 5 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        // We expect: 1 offer + 3 chunks (64KB, 64KB, 22KB) + 1 file.end = 5 messages
        Assert.Equal(5, sentMessages.Count);

        var reconstructed = new MemoryStream();
        for (var i = 1; i <= 3; i++)
        {
            using var chunkDoc = JsonDocument.Parse(sentMessages[i]);
            Assert.Equal(ProtocolV1.Message.FileChunk, chunkDoc.RootElement.GetProperty("t").GetString());
            Assert.Equal(i - 1, chunkDoc.RootElement.GetProperty("seq").GetInt32());
            var base64 = chunkDoc.RootElement.GetProperty("data").GetString()!;
            var chunkBytes = Convert.FromBase64String(base64);
            reconstructed.Write(chunkBytes);
        }

        Assert.Equal(data, reconstructed.ToArray());

        // Verify file.end
        using var endDoc = JsonDocument.Parse(sentMessages[4]);
        Assert.Equal(ProtocolV1.Message.FileEnd, endDoc.RootElement.GetProperty("t").GetString());
        Assert.Equal(expectedHash, endDoc.RootElement.GetProperty("sha256").GetString());

        // 3. Simulate peer file.done
        var doneDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            t = ProtocolV1.Message.FileDone,
            id = transferId,
            ok = true
        }));
        await service.HandlePeerMessageAsync(ProtocolV1.Message.FileDone, doneDoc.RootElement);

        var snapshot = service.Snapshot;
        var recent = Assert.Single(snapshot.RecentTransfers);
        Assert.Equal(FileTransferState.Completed, recent.State);
        Assert.Equal(data.Length, recent.TransferredBytes);
    }

    [Fact]
    public async Task ReceiveFileReassemblesChunksAndVerifiesIntegrity()
    {
        var sentMessages = new List<string>();
        Task<bool> MockSend(string msg, CancellationToken ct)
        {
            sentMessages.Add(msg);
            return Task.FromResult(true);
        }

        using var service = new FileTransferService(_tempDir, "test-pc", MockSend);
        await service.SetSessionActiveAsync(true);

        var downloadDir = Path.Combine(_tempDir, "Downloads");
        await service.SetOptionsAsync(new FileTransferOptions(Enabled: true, DownloadDirectory: downloadDir));

        var data = Encoding.UTF8.GetBytes("Hello from Android companion! This is a native file transfer test.");
        var expectedHash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        const string transferId = "test-rx-transfer-001";

        // 1. Peer sends file.offer
        var offerDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            t = ProtocolV1.Message.FileOffer,
            id = transferId,
            name = "notes.txt",
            size = data.Length
        }));
        await service.HandlePeerMessageAsync(ProtocolV1.Message.FileOffer, offerDoc.RootElement);

        // Verify service replied with file.accept
        Assert.Single(sentMessages);
        using var acceptDoc = JsonDocument.Parse(sentMessages[0]);
        Assert.Equal(ProtocolV1.Message.FileAccept, acceptDoc.RootElement.GetProperty("t").GetString());
        Assert.Equal(transferId, acceptDoc.RootElement.GetProperty("id").GetString());

        // 2. Peer sends chunk
        var chunkDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            t = ProtocolV1.Message.FileChunk,
            id = transferId,
            seq = 0,
            data = Convert.ToBase64String(data)
        }));
        await service.HandlePeerMessageAsync(ProtocolV1.Message.FileChunk, chunkDoc.RootElement);

        // 3. Peer sends file.end
        var endDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            t = ProtocolV1.Message.FileEnd,
            id = transferId,
            sha256 = expectedHash
        }));
        await service.HandlePeerMessageAsync(ProtocolV1.Message.FileEnd, endDoc.RootElement);

        // Verify service replied with file.done (ok = true)
        Assert.Equal(2, sentMessages.Count);
        using var doneDoc = JsonDocument.Parse(sentMessages[1]);
        Assert.Equal(ProtocolV1.Message.FileDone, doneDoc.RootElement.GetProperty("t").GetString());
        Assert.True(doneDoc.RootElement.GetProperty("ok").GetBoolean());
        var savedPath = doneDoc.RootElement.GetProperty("path").GetString();
        Assert.NotNull(savedPath);
        Assert.True(File.Exists(savedPath));
        Assert.Equal(data, await File.ReadAllBytesAsync(savedPath));
    }

    [Fact]
    public async Task ReceiveFileRejectsCorruptChecksum()
    {
        var sentMessages = new List<string>();
        Task<bool> MockSend(string msg, CancellationToken ct)
        {
            sentMessages.Add(msg);
            return Task.FromResult(true);
        }

        using var service = new FileTransferService(_tempDir, "test-pc", MockSend);
        await service.SetSessionActiveAsync(true);
        await service.SetOptionsAsync(new FileTransferOptions(Enabled: true, DownloadDirectory: Path.Combine(_tempDir, "Downloads")));

        var data = Encoding.UTF8.GetBytes("Data with bad checksum test");
        const string transferId = "test-bad-checksum";

        // Offer
        var offerDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            t = ProtocolV1.Message.FileOffer,
            id = transferId,
            name = "bad.txt",
            size = data.Length
        }));
        await service.HandlePeerMessageAsync(ProtocolV1.Message.FileOffer, offerDoc.RootElement);

        // Chunk
        var chunkDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            t = ProtocolV1.Message.FileChunk,
            id = transferId,
            seq = 0,
            data = Convert.ToBase64String(data)
        }));
        await service.HandlePeerMessageAsync(ProtocolV1.Message.FileChunk, chunkDoc.RootElement);

        // End with deliberate bogus checksum
        var endDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            t = ProtocolV1.Message.FileEnd,
            id = transferId,
            sha256 = "0000000000000000000000000000000000000000000000000000000000000000"
        }));
        await service.HandlePeerMessageAsync(ProtocolV1.Message.FileEnd, endDoc.RootElement);

        // Check reply is file.done with ok: false
        Assert.Equal(2, sentMessages.Count);
        using var doneDoc = JsonDocument.Parse(sentMessages[1]);
        Assert.Equal(ProtocolV1.Message.FileDone, doneDoc.RootElement.GetProperty("t").GetString());
        Assert.False(doneDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("checksum mismatch", doneDoc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ReceiveFileRejectsOutOfOrderChunkSequence()
    {
        var sentMessages = new List<string>();
        Task<bool> MockSend(string msg, CancellationToken ct)
        {
            sentMessages.Add(msg);
            return Task.FromResult(true);
        }

        using var service = new FileTransferService(_tempDir, "test-pc", MockSend);
        await service.SetSessionActiveAsync(true);
        await service.SetOptionsAsync(new FileTransferOptions(Enabled: true, DownloadDirectory: Path.Combine(_tempDir, "Downloads")));

        const string transferId = "test-seq-error";

        // Offer
        var offerDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            t = ProtocolV1.Message.FileOffer,
            id = transferId,
            name = "seq.txt",
            size = 100
        }));
        await service.HandlePeerMessageAsync(ProtocolV1.Message.FileOffer, offerDoc.RootElement);

        // Send seq 3 instead of 0
        var badChunk = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            t = ProtocolV1.Message.FileChunk,
            id = transferId,
            seq = 3,
            data = Convert.ToBase64String(new byte[10])
        }));
        await service.HandlePeerMessageAsync(ProtocolV1.Message.FileChunk, badChunk.RootElement);

        // Check cancellation was sent
        Assert.Equal(2, sentMessages.Count);
        using var cancelDoc = JsonDocument.Parse(sentMessages[1]);
        Assert.Equal(ProtocolV1.Message.FileCancel, cancelDoc.RootElement.GetProperty("t").GetString());
    }

    [Fact]
    public async Task CancelTransferNotifiesPeerAndUpdatesState()
    {
        var sentMessages = new List<string>();
        Task<bool> MockSend(string msg, CancellationToken ct)
        {
            sentMessages.Add(msg);
            return Task.FromResult(true);
        }

        using var service = new FileTransferService(_tempDir, "test-pc", MockSend);
        await service.SetSessionActiveAsync(true);

        var sourceFile = Path.Combine(_tempDir, "cancel_test.txt");
        await File.WriteAllTextAsync(sourceFile, "Cancel me");

        var transferId = await service.StartSendFileAsync(sourceFile);
        await service.CancelTransferAsync(transferId);

        Assert.Equal(2, sentMessages.Count);
        using var cancelDoc = JsonDocument.Parse(sentMessages[1]);
        Assert.Equal(ProtocolV1.Message.FileCancel, cancelDoc.RootElement.GetProperty("t").GetString());

        var snapshot = service.Snapshot;
        var item = Assert.Single(snapshot.RecentTransfers);
        Assert.Equal(FileTransferState.Canceled, item.State);
    }
}
