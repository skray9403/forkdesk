using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace TwinDesk.Native.Core.Discovery;

/// <summary>
/// Native UDP discovery using the existing TwinDesk protocol-v1 beacon format.
/// Only Windows and Android peers are surfaced by this Windows product.
/// Pairing and authenticated sessions are separate services.
/// </summary>
public sealed class PeerDiscoveryService : IAsyncDisposable
{
    private readonly DiscoveryOptions _options;
    private readonly ConcurrentDictionary<string, NearbyPeer> _peers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private UdpClient? _socket;
    private CancellationTokenSource? _stop;
    private Task? _receiveTask;
    private Task? _beaconTask;
    private bool _disposed;

    public event EventHandler<IReadOnlyList<NearbyPeer>>? PeersChanged;
    public event EventHandler<string>? StatusChanged;

    public PeerDiscoveryService(DiscoveryOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    public IReadOnlyList<NearbyPeer> Snapshot() =>
        _peers.Values.OrderByDescending(p => p.LastSeen).ThenBy(p => p.Name)
            .ToArray();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_socket is not null) return;

            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _socket = new UdpClient(AddressFamily.InterNetwork);
            _socket.EnableBroadcast = true;
            _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, ProtocolV1.DiscoveryPort));

            var token = _stop.Token;
            _receiveTask = ReceiveLoopAsync(token);
            _beaconTask = BeaconLoopAsync(token);
            StatusChanged?.Invoke(this, "Listening for Windows and Android peers");
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var stale = _peers.Values.Where(p => DateTimeOffset.UtcNow - p.LastSeen > _options.StaleAfter)
            .Select(p => p.Id).ToArray();
        foreach (var id in stale) _peers.TryRemove(id, out _);

        await SendProbeAsync(cancellationToken).ConfigureAwait(false);
        await SendBeaconAsync(cancellationToken).ConfigureAwait(false);
        Publish();
    }

    private async Task BeaconLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.BeaconInterval);
        do
        {
            try
            {
                await SendBeaconAsync(cancellationToken).ConfigureAwait(false);
                PruneStale();
                await SendProbeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                StatusChanged?.Invoke(this, $"Discovery send failed: {ex.SocketErrorCode}");
            }
        } while (await WaitNextAsync(timer, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitNextAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try { return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
    }

    private async Task SendBeaconAsync(CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null) return;
        var beacon = new PeerBeacon
        {
            Type = ProtocolV1.Message.Beacon,
            Version = ProtocolV1.Version,
            Id = _options.DeviceId,
            Name = _options.DeviceName,
            Os = ProtocolV1.PeerOs.Windows,
            Role = "host",
            Port = _options.Port,
            Fingerprint = _options.Fingerprint
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(beacon);
        foreach (var target in BroadcastTargets())
            await socket.SendAsync(bytes, new IPEndPoint(target, ProtocolV1.DiscoveryPort), cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task SendProbeAsync(CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null) return;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            t = ProtocolV1.Message.Probe,
            v = ProtocolV1.Version,
            id = _options.DeviceId
        });
        foreach (var target in BroadcastTargets())
            await socket.SendAsync(payload, new IPEndPoint(target, ProtocolV1.DiscoveryPort), cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null) return;

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                StatusChanged?.Invoke(this, $"Discovery receive failed: {ex.SocketErrorCode}");
                break;
            }

            if (!PeerBeacon.TryParse(packet.Buffer, out var beacon) || beacon is null)
                continue;
            if (beacon.Id == _options.DeviceId)
                continue;

            if (beacon.Type == ProtocolV1.Message.Probe)
            {
                await SendBeaconToAsync(packet.RemoteEndPoint.Address, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!PeerPlatformPolicy.TryParseWireId(beacon.Os, out var platform))
                continue;

            var now = DateTimeOffset.UtcNow;
            var candidate = new NearbyPeer(
                beacon.Id,
                string.IsNullOrWhiteSpace(beacon.Name) ? "TwinDesk device" : beacon.Name[..Math.Min(100, beacon.Name.Length)],
                platform,
                packet.RemoteEndPoint.Address.ToString(),
                beacon.Port is > 0 and <= 65535 ? beacon.Port : ProtocolV1.Port,
                beacon.Fingerprint,
                false,
                false,
                now);

            _peers.AddOrUpdate(candidate.Id, candidate, (_, old) => candidate with
            {
                IsPaired = old.IsPaired,
                IsConnected = old.IsConnected
            });
            Publish();
        }
    }

    private async Task SendBeaconToAsync(IPAddress address, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null) return;
        var beacon = new PeerBeacon
        {
            Id = _options.DeviceId,
            Name = _options.DeviceName,
            Os = ProtocolV1.PeerOs.Windows,
            Port = _options.Port,
            Fingerprint = _options.Fingerprint
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(beacon);
        await socket.SendAsync(bytes, new IPEndPoint(address, ProtocolV1.DiscoveryPort), cancellationToken)
            .ConfigureAwait(false);
    }

    private IEnumerable<IPAddress> BroadcastTargets()
    {
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var properties = nic.GetIPProperties();
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    unicast.IPv4Mask is null)
                    continue;

                var address = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++)
                    broadcast[i] = (byte)(address[i] | ~mask[i]);
                targets.Add(new IPAddress(broadcast));
            }
        }
        return targets;
    }

    private void PruneStale()
    {
        var cutoff = DateTimeOffset.UtcNow - _options.StaleAfter;
        var changed = false;
        foreach (var (id, peer) in _peers)
            if (peer.LastSeen < cutoff)
                changed |= _peers.TryRemove(id, out _);
        if (changed) Publish();
    }

    private void Publish() => PeersChanged?.Invoke(this, Snapshot());

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var stop = _stop;
        _stop = null;
        if (stop is not null)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            _socket?.Dispose();
            _socket = null;
            var tasks = new[] { _receiveTask, _beaconTask }.Where(t => t is not null).Cast<Task>().ToArray();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            stop.Dispose();
        }
        _receiveTask = null;
        _beaconTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _lifecycle.Dispose();
    }
}
