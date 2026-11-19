using System.Text.Json;
using TwinDesk.Native.Core;
using Xunit;

namespace TwinDesk.Native.Tests;

public sealed class FeatureServicesTests
{
    [Fact]
    public async Task RemoteInputServiceSendsStateOnSessionStarted()
    {
        var sentMessages = new List<string>();
        var service = new NativeRemoteInputService((msg, _) =>
        {
            sentMessages.Add(msg);
            return Task.FromResult(true);
        });

        await service.HandleSessionStartedAsync();

        Assert.Single(sentMessages);
        using var doc = JsonDocument.Parse(sentMessages[0]);
        Assert.Equal("remote.state", doc.RootElement.GetProperty("t").GetString());
        Assert.True(doc.RootElement.GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task RemoteInputServiceUpdatesSnapshotOnMouseInput()
    {
        var sentMessages = new List<string>();
        var service = new NativeRemoteInputService((msg, _) =>
        {
            sentMessages.Add(msg);
            return Task.FromResult(true);
        });

        using var mmDoc = JsonDocument.Parse("{\"t\":\"remote.mm\",\"dx\":10,\"dy\":-5}");
        await service.HandlePeerMessageAsync("remote.mm", mmDoc.RootElement);

        var snap = service.Snapshot;
        Assert.Contains("Mouse move (10, -5)", snap.LastEvent);

        using var mdDoc = JsonDocument.Parse("{\"t\":\"remote.md\",\"b\":0}");
        await service.HandlePeerMessageAsync("remote.md", mdDoc.RootElement);
        Assert.Contains("Mouse button 0 down", service.Snapshot.LastEvent);
    }

    [Fact]
    public async Task NotificationServiceRecordsIncomingAlertsAndSendsPolicy()
    {
        var sentMessages = new List<string>();
        var service = new NativeNotificationService((msg, _) =>
        {
            sentMessages.Add(msg);
            return Task.FromResult(true);
        });

        await service.HandleSessionStartedAsync();
        Assert.Single(sentMessages);
        using (var policyDoc = JsonDocument.Parse(sentMessages[0]))
        {
            Assert.Equal("notif.policy", policyDoc.RootElement.GetProperty("t").GetString());
            Assert.True(policyDoc.RootElement.GetProperty("accept").GetBoolean());
        }

        using var notifDoc = JsonDocument.Parse("{\"t\":\"notif\",\"id\":\"alert-1\",\"appName\":\"Signal\",\"title\":\"Alice\",\"body\":\"Hello TwinDesk!\",\"at\":1700000000000}");
        await service.HandlePeerMessageAsync("notif", notifDoc.RootElement);

        var snap = service.Snapshot;
        Assert.Single(snap.RecentNotifications);
        Assert.Equal("alert-1", snap.RecentNotifications[0].Id);
        Assert.Equal("Signal", snap.RecentNotifications[0].AppName);
        Assert.Equal("Alice", snap.RecentNotifications[0].Title);
        Assert.Equal("Hello TwinDesk!", snap.RecentNotifications[0].Body);

        // Test Dismissal
        await service.DismissNotificationAsync("alert-1");
        Assert.Empty(service.Snapshot.RecentNotifications);
        Assert.Equal(2, sentMessages.Count);
        using var dismissDoc = JsonDocument.Parse(sentMessages[1]);
        Assert.Equal("notif.dismiss", dismissDoc.RootElement.GetProperty("t").GetString());
        Assert.Equal("alert-1", dismissDoc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task AudioRoutingServiceSendsWantMessageOnOptionChanged()
    {
        var sentMessages = new List<string>();
        var service = new NativeAudioRoutingService((msg, _) =>
        {
            sentMessages.Add(msg);
            return Task.FromResult(true);
        });

        await service.SetOptionsAsync(new AudioRoutingOptions(PlayPhoneSound: true, VolumePercent: 80));

        Assert.Single(sentMessages);
        using var doc = JsonDocument.Parse(sentMessages[0]);
        Assert.Equal("phone.sound.want", doc.RootElement.GetProperty("t").GetString());
        Assert.True(doc.RootElement.GetProperty("on").GetBoolean());
        Assert.True(service.Snapshot.PlayPhoneSound);
        Assert.Equal(80, service.Snapshot.VolumePercent);
    }
}
