using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace IntercomCompanion.Core;

/// <summary>
/// Background UDP peer. It owns LAN discovery and addressed configuration;
/// session/audio state is added separately so receiving packets never blocks UI.
/// </summary>
internal sealed class IntercomNode : IAsyncDisposable
{
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("239.255.42.99");
    private static readonly TimeSpan HelloInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PeerLifetime = TimeSpan.FromSeconds(10);

    private readonly CompanionSettings settings;
    private readonly ConcurrentDictionary<uint, Peer> peers = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly UdpClient client;
    private Task? receiveTask;
    private Task? helloTask;
    private Task? expiryTask;

    public IntercomNode(CompanionSettings settings)
    {
        this.settings = settings;
        client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.Port));
        client.JoinMulticastGroup(MulticastGroup, 1);
        client.Ttl = 1;
    }

    public uint NodeId => settings.NodeId;
    public string Alias => settings.Alias;
    public IReadOnlyCollection<Peer> Peers => peers.Values.ToArray();

    /// <summary>Stable destination set for a single PTT session.</summary>
    public IReadOnlyList<Peer> SnapshotActivePeers()
    {
        var expiry = DateTimeOffset.UtcNow - PeerLifetime;
        return peers.Values.Where(peer => peer.LastSeen >= expiry).ToArray();
    }

    public event EventHandler? PeersChanged;
    public event EventHandler<DeviceConfigurationReceivedEventArgs>? ConfigurationReceived;
    public event Action<string>? Diagnostic;
    public event EventHandler<IntercomPacketReceivedEventArgs>? PacketReceived;

    public void Start()
    {
        receiveTask = Task.Run(ReceiveLoopAsync);
        helloTask = Task.Run(HelloLoopAsync);
        expiryTask = Task.Run(ExpiryLoopAsync);
        _ = SendHelloAsync(stopping.Token);
    }

    public void SetAlias(string alias)
    {
        settings.Alias = CompanionSettings.SanitizeAlias(alias);
        settings.Save();
        _ = SendHelloAsync(stopping.Token);
    }

    public async Task RequestConfigurationAsync(Peer peer, CancellationToken cancellationToken = default)
    {
        await SendToEndpointAsync(PacketType.ConfigGet, NewSessionId(), 0,
            Encoding.UTF8.GetBytes("{\"cmd\":\"get\"}"), peer.Endpoint, cancellationToken: cancellationToken);
    }

    public async Task SetConfigurationAsync(Peer peer, DeviceConfiguration configuration,
                                            CancellationToken cancellationToken = default)
    {
        var request = JsonSerializer.Serialize(new
        {
            cmd = "set",
            alias = configuration.Alias,
            speaker_volume = configuration.SpeakerVolume,
            led_brightness = configuration.LedBrightness,
            buttons_swapped = configuration.ButtonsSwapped,
            ring_orientation = configuration.RingOrientation,
        });
        await SendToEndpointAsync(PacketType.ConfigSet, NewSessionId(), 0,
            Encoding.UTF8.GetBytes(request), peer.Endpoint, cancellationToken: cancellationToken);
    }

    public Task SendToEndpointAsync(PacketType type, uint session, uint sequence, byte[] payload,
                                    IPEndPoint destination, byte flags = 0,
                                    CancellationToken cancellationToken = default) =>
        SendAsync(type, session, sequence, payload, destination, flags, cancellationToken);

    public async Task SendToPeersAsync(PacketType type, uint session, uint sequence, byte[] payload,
                                       IEnumerable<Peer> destinations, byte flags = 0,
                                       CancellationToken cancellationToken = default)
    {
        foreach (var peer in destinations)
            await SendAsync(type, session, sequence, payload, peer.Endpoint, flags, cancellationToken);
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var received = await client.ReceiveAsync(stopping.Token);
                if (!Protocol.TryParse(received.Buffer, NodeId, out var packet) || packet is null)
                    continue;

                LearnPeer(packet, received.RemoteEndPoint);
                PacketReceived?.Invoke(this, new IntercomPacketReceivedEventArgs(packet, received.RemoteEndPoint));
                if (packet.Type == PacketType.ConfigReply)
                    ParseConfigurationReply(packet, received.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException exception) when (!stopping.IsCancellationRequested)
        {
            Diagnostic?.Invoke($"Network receive stopped: {exception.SocketErrorCode}");
        }
    }

    private async Task HelloLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(HelloInterval);
            while (await timer.WaitForNextTickAsync(stopping.Token))
                await SendHelloAsync(stopping.Token);
        }
        catch (OperationCanceledException) { }
    }

    private async Task ExpiryLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stopping.Token))
            {
                var expiry = DateTimeOffset.UtcNow - PeerLifetime;
                var changed = false;
                foreach (var (nodeId, peer) in peers)
                    changed |= peer.LastSeen < expiry && peers.TryRemove(nodeId, out _);
                if (changed) PeersChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void LearnPeer(IntercomPacket packet, IPEndPoint source)
    {
        peers.TryGetValue(packet.SenderId, out var known);
        var alias = packet.Type == PacketType.Hello
            ? CompanionSettings.SanitizeAlias(Encoding.UTF8.GetString(packet.Payload))
            : known?.Alias ?? $"Device {packet.SenderId:x8}";
        var peer = new Peer(packet.SenderId, source, alias, DateTimeOffset.UtcNow);
        peers[packet.SenderId] = peer;
        if (known is null || known.Alias != peer.Alias || !known.Endpoint.Equals(peer.Endpoint))
            PeersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ParseConfigurationReply(IntercomPacket packet, IPEndPoint source)
    {
        try
        {
            using var json = JsonDocument.Parse(packet.Payload);
            var root = json.RootElement;
            var config = new DeviceConfiguration(
                root.TryGetProperty("alias", out var alias) ? alias.GetString() ?? "" : "",
                root.TryGetProperty("speaker_volume", out var volume) ? volume.GetInt32() : 512,
                root.TryGetProperty("led_brightness", out var brightness) ? brightness.GetInt32() : 48,
                root.TryGetProperty("buttons_swapped", out var swapped) && swapped.GetBoolean(),
                root.TryGetProperty("ring_orientation", out var orientation) ? orientation.GetInt32() : 0);
            ConfigurationReceived?.Invoke(this,
                new DeviceConfigurationReceivedEventArgs(packet.SenderId, source, config));
        }
        catch (JsonException)
        {
            Diagnostic?.Invoke($"Ignored invalid configuration reply from {source.Address}.");
        }
    }

    private Task SendHelloAsync(CancellationToken cancellationToken) => SendAsync(
        PacketType.Hello, 0, 0, Encoding.UTF8.GetBytes(Alias),
        new IPEndPoint(MulticastGroup, Protocol.Port), 0, cancellationToken);

    private async Task SendAsync(PacketType type, uint session, uint sequence, byte[] payload,
                                 IPEndPoint destination, byte flags,
                                 CancellationToken cancellationToken)
    {
        var datagram = Protocol.Pack(NodeId, type, session, sequence, TimestampMs(), payload, flags);
        await sendGate.WaitAsync(cancellationToken);
        try { await client.SendAsync(datagram, destination, cancellationToken); }
        finally { sendGate.Release(); }
    }

    private static uint TimestampMs() => unchecked((uint)Environment.TickCount64);
    public static uint NewSessionId()
    {
        var value = BitConverter.ToUInt32(Guid.NewGuid().ToByteArray());
        return value == 0 ? 1u : value;
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        var tasks = new[] { receiveTask, helloTask, expiryTask }.OfType<Task>();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
        sendGate.Dispose();
        stopping.Dispose();
    }

    /// <summary>Non-blocking shutdown for the WinForms UI thread.</summary>
    public void Stop()
    {
        if (!stopping.IsCancellationRequested) stopping.Cancel();
        client.Dispose();
    }
}

internal sealed class DeviceConfigurationReceivedEventArgs(uint nodeId, IPEndPoint endpoint,
                                                            DeviceConfiguration configuration) : EventArgs
{
    public uint NodeId { get; } = nodeId;
    public IPEndPoint Endpoint { get; } = endpoint;
    public DeviceConfiguration Configuration { get; } = configuration;
}

internal sealed class IntercomPacketReceivedEventArgs(IntercomPacket packet, IPEndPoint endpoint) : EventArgs
{
    public IntercomPacket Packet { get; } = packet;
    public IPEndPoint Endpoint { get; } = endpoint;
}
