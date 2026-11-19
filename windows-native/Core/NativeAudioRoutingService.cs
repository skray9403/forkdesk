using System.Text.Json;

namespace TwinDesk.Native.Core;

public sealed record AudioRoutingOptions(
    bool PlayPhoneSound = false,
    int VolumePercent = 100,
    bool ForwardMicrophone = false);

public sealed record AudioRoutingSnapshot(
    bool PlayPhoneSound,
    int VolumePercent,
    bool ForwardMicrophone,
    string Status);

public sealed class NativeAudioRoutingService
{
    private readonly Func<string, CancellationToken, Task<bool>> _sendPeerMessage;
    private AudioRoutingOptions _options = new();
    private string _status = "Audio receiver ready";
    private readonly object _sync = new();

    public event Action<AudioRoutingSnapshot>? SnapshotChanged;

    public NativeAudioRoutingService(Func<string, CancellationToken, Task<bool>> sendPeerMessage)
    {
        _sendPeerMessage = sendPeerMessage ?? throw new ArgumentNullException(nameof(sendPeerMessage));
    }

    public AudioRoutingSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return new AudioRoutingSnapshot(_options.PlayPhoneSound, _options.VolumePercent, _options.ForwardMicrophone, _status);
            }
        }
    }

    public async Task SetOptionsAsync(AudioRoutingOptions options, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _options = options;
        }
        await SendSoundPolicyAsync(cancellationToken).ConfigureAwait(false);
        SnapshotChanged?.Invoke(Snapshot);
    }

    public async Task HandleSessionStartedAsync(CancellationToken cancellationToken = default)
    {
        await SendSoundPolicyAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SendSoundPolicyAsync(CancellationToken cancellationToken = default)
    {
        var msg = JsonSerializer.Serialize(new
        {
            t = "phone.sound.want",
            on = _options.PlayPhoneSound
        });
        await _sendPeerMessage(msg, cancellationToken).ConfigureAwait(false);
    }

    public Task HandlePeerMessageAsync(string type, JsonElement message, CancellationToken cancellationToken = default)
    {
        if (type == "phone.sound")
        {
            lock (_sync)
            {
                _status = "Receiving audio stream";
            }
            SnapshotChanged?.Invoke(Snapshot);
        }
        return Task.CompletedTask;
    }
}
