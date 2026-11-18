using System.Text.Json;
using TwinDesk.Native.Core;
using Xunit;

namespace TwinDesk.Native.Tests;

public sealed class ClipboardSyncServiceTests
{
    [Fact]
    public async Task SyncIsOffByDefault()
    {
        var root = TempDirectory();
        using var adapter = new FakeAdapter();
        var sent = new List<string>();
        using var service = Create(root, adapter, payload => { sent.Add(payload); return Task.FromResult(true); });
        Assert.False(service.Snapshot.Options.Enabled);
        await service.SetSessionActiveAsync(true);
        using var policy = JsonDocument.Parse(sent.Single());
        Assert.False(policy.RootElement.GetProperty("accept").GetBoolean());
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task SendsLocalTextOnlyAfterBothSidesAllowIt()
    {
        var root = TempDirectory();
        using var adapter = new FakeAdapter();
        var sent = new List<string>();
        using var service = Create(root, adapter, payload => { sent.Add(payload); return Task.FromResult(true); });
        await service.SetOptionsAsync(new ClipboardSyncOptions(true, true, true));
        await service.SetSessionActiveAsync(true);
        await service.HandlePeerMessageAsync(ProtocolV1.Message.ClipPolicy, Parse("{\"accept\":true}").RootElement);
        adapter.SetText("from Windows");
        await WaitForAsync(() => sent.Any(x => IsMessage(x, ProtocolV1.Message.Clip)));
        using var clip = JsonDocument.Parse(sent.Single(x => IsMessage(x, ProtocolV1.Message.Clip)));
        Assert.Equal("from Windows", clip.RootElement.GetProperty("text").GetString());
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task ReceivedTextIsNotEchoedBack()
    {
        var root = TempDirectory();
        using var adapter = new FakeAdapter();
        var sent = new List<string>();
        using var service = Create(root, adapter, payload => { sent.Add(payload); return Task.FromResult(true); });
        await service.SetOptionsAsync(new ClipboardSyncOptions(true, true, true));
        await service.SetSessionActiveAsync(true);
        await service.HandlePeerMessageAsync(ProtocolV1.Message.ClipPolicy, Parse("{\"accept\":true}").RootElement);
        sent.Clear();
        await service.HandlePeerMessageAsync(ProtocolV1.Message.Clip, Parse("{\"text\":\"from Android\"}").RootElement);
        await Task.Delay(100);
        Assert.Equal("from Android", adapter.Text);
        Assert.Empty(sent);
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task OversizedTextIsRejected()
    {
        var root = TempDirectory();
        using var adapter = new FakeAdapter();
        var sent = new List<string>();
        using var service = Create(root, adapter, payload => { sent.Add(payload); return Task.FromResult(true); });
        await service.SetOptionsAsync(new ClipboardSyncOptions(true, true, true));
        await service.SetSessionActiveAsync(true);
        await service.HandlePeerMessageAsync(ProtocolV1.Message.ClipPolicy, Parse("{\"accept\":true}").RootElement);
        sent.Clear();
        adapter.SetText(new string('x', ProtocolV1.ClipboardMaxBytes + 1));
        await Task.Delay(100);
        Assert.Empty(sent);
        Assert.Contains("exceeds", service.Snapshot.Status);
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task PreferencesPersistAcrossInstances()
    {
        var root = TempDirectory();
        using (var adapter = new FakeAdapter())
        using (var first = Create(root, adapter, _ => Task.FromResult(true)))
            await first.SetOptionsAsync(new ClipboardSyncOptions(true, false, true));
        using var secondAdapter = new FakeAdapter();
        using var second = Create(root, secondAdapter, _ => Task.FromResult(true));
        Assert.Equal(new ClipboardSyncOptions(true, false, true), second.Snapshot.Options);
        Directory.Delete(root, true);
    }

    private static ClipboardSyncService Create(string root, FakeAdapter adapter, Func<string, Task<bool>> send) =>
        new(adapter, root, "windows-test-device", (payload, _) => send(payload));
    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);
    private static bool IsMessage(string json, string type)
    {
        using var document = Parse(json);
        return document.RootElement.GetProperty("t").GetString() == type;
    }
    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "TwinDesk.Native.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        Assert.True(predicate(), "Timed out waiting for clipboard message.");
    }
    private sealed class FakeAdapter : IClipboardAdapter
    {
        public event EventHandler? ContentChanged;
        public string? Text { get; private set; }

        public Task<string?> ReadTextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Text);
        public Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
        {
            Text = text;
            ContentChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        public void SetText(string text)
        {
            Text = text;
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }
        public void Dispose() => ContentChanged = null;
    }
}