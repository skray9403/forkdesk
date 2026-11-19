using System.Text.Json;

namespace TwinDesk.Native.Core;

public sealed record PhoneNotification(
    string Id,
    string App,
    string AppName,
    string Title,
    string Subtitle,
    string Body,
    DateTimeOffset Timestamp);

public sealed record NotificationSnapshot(
    bool Enabled,
    IReadOnlyList<PhoneNotification> RecentNotifications);

public sealed class NativeNotificationService
{
    private readonly Func<string, CancellationToken, Task<bool>> _sendPeerMessage;
    private bool _enabled = true;
    private readonly List<PhoneNotification> _notifications = [];
    private readonly object _sync = new();

    public event Action<NotificationSnapshot>? SnapshotChanged;

    public NativeNotificationService(Func<string, CancellationToken, Task<bool>> sendPeerMessage)
    {
        _sendPeerMessage = sendPeerMessage ?? throw new ArgumentNullException(nameof(sendPeerMessage));
    }

    public NotificationSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return new NotificationSnapshot(_enabled, _notifications.ToArray());
            }
        }
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _enabled = enabled;
        await SendPolicyAsync(cancellationToken).ConfigureAwait(false);
        SnapshotChanged?.Invoke(Snapshot);
    }

    public async Task HandleSessionStartedAsync(CancellationToken cancellationToken = default)
    {
        await SendPolicyAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SendPolicyAsync(CancellationToken cancellationToken = default)
    {
        var policy = JsonSerializer.Serialize(new
        {
            t = "notif.policy",
            accept = _enabled,
            rich = true
        });
        await _sendPeerMessage(policy, cancellationToken).ConfigureAwait(false);
    }

    public Task HandlePeerMessageAsync(string type, JsonElement message, CancellationToken cancellationToken = default)
    {
        if (type == "notif" && _enabled)
        {
            var id = message.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
            var app = message.TryGetProperty("app", out var appEl) ? appEl.GetString() ?? "" : "";
            var appName = message.TryGetProperty("appName", out var appNameEl) ? appNameEl.GetString() ?? app : app;
            var title = message.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
            var subtitle = message.TryGetProperty("subtitle", out var subEl) ? subEl.GetString() ?? "" : "";
            var body = message.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
            var atMs = message.TryGetProperty("at", out var atEl) && atEl.TryGetInt64(out var ms) ? ms : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var item = new PhoneNotification(id, app, appName, title, subtitle, body, DateTimeOffset.FromUnixTimeMilliseconds(atMs));
            lock (_sync)
            {
                _notifications.RemoveAll(x => x.Id == id);
                _notifications.Insert(0, item);
                if (_notifications.Count > 50)
                    _notifications.RemoveRange(50, _notifications.Count - 50);
            }
            SnapshotChanged?.Invoke(Snapshot);
        }
        return Task.CompletedTask;
    }

    public async Task DismissNotificationAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _notifications.RemoveAll(x => x.Id == id);
        }
        SnapshotChanged?.Invoke(Snapshot);

        var dismissMsg = JsonSerializer.Serialize(new
        {
            t = "notif.dismiss",
            id
        });
        await _sendPeerMessage(dismissMsg, cancellationToken).ConfigureAwait(false);
    }

    public void ClearAll()
    {
        lock (_sync)
        {
            _notifications.Clear();
        }
        SnapshotChanged?.Invoke(Snapshot);
    }
}
