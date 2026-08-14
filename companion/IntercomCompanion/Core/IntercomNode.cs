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
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<DeviceConfigurationReceivedEventArgs>>
        configurationRequests = new();
    private readonly ConcurrentDictionary<uint, OtaOperation> otaOperations = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly SemaphoreSlim mediaSendGate = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly UdpClient client;
    private readonly UdpClient mediaClient;
    private readonly IPAddress discoveryInterface;
    private DateTimeOffset nextSubnetProbeAt = DateTimeOffset.MinValue;
    private Task? receiveTask;
    private Task? mediaReceiveTask;
    private Task? helloTask;
    private Task? expiryTask;

    public IntercomNode(CompanionSettings settings)
    {
        this.settings = settings;
        client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.EnableBroadcast = true;
        // Do not pin discovery to a Windows interface index. Interface indexes
        // change whenever adapters are added/reconfigured; index 1 is commonly
        // the loopback adapter, which makes the companion invisible to LAN peers.
        // Instead, resolve the local IPv4 address used by Windows' default route
        // and join discovery on that Wi-Fi/Ethernet interface.
        discoveryInterface = GetDefaultRouteAddress();
        // Binding to Any lets Windows select a virtual or loopback source even
        // when membership is correct. Binding the control socket to the chosen
        // interface makes both HELLO egress and unicast replies use the LAN NIC.
        client.Client.Bind(new IPEndPoint(discoveryInterface, Protocol.Port));
        client.JoinMulticastGroup(MulticastGroup, discoveryInterface);
        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
            discoveryInterface.GetAddressBytes());
        DiagnosticLog.Write($"discovery multicast interface={discoveryInterface}");
        client.Ttl = 1;

        mediaClient = new UdpClient(AddressFamily.InterNetwork);
        mediaClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        SetVideoPriority(mediaClient.Client);
        mediaClient.Client.Bind(new IPEndPoint(IPAddress.Any, Rtp.Port));
    }

    public uint NodeId => settings.NodeId;
    public string MeshId => settings.MeshId;
    public string Alias => settings.Alias;
    public IPAddress DiscoveryInterface => discoveryInterface;
    public IReadOnlyCollection<Peer> Peers => peers.Values.ToArray();

    /// <summary>Stable destination set for a single PTT session.</summary>
    public IReadOnlyList<Peer> SnapshotActivePeers()
    {
        var expiry = DateTimeOffset.UtcNow - PeerLifetime;
        return peers.Values.Where(peer => peer.LastSeen >= expiry).ToArray();
    }

    public event EventHandler? PeersChanged;
    public event Action<string>? Diagnostic;
    public event EventHandler<IntercomPacketReceivedEventArgs>? PacketReceived;
    public event EventHandler<RtpPacketReceivedEventArgs>? RtpPacketReceived;
    public event EventHandler<OtaStatusReceivedEventArgs>? OtaStatusReceived;

    public void Start()
    {
        receiveTask = Task.Run(ReceiveLoopAsync);
        mediaReceiveTask = Task.Run(MediaReceiveLoopAsync);
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

    public void SetMeshId(string meshId)
    {
        if (!Protocol.IsValidMeshId(meshId))
            throw new ArgumentException("Group ID must be exactly four A–Z or 0–9 characters.", nameof(meshId));
        settings.MeshId = meshId;
        settings.Save();
        peers.Clear();
        PeersChanged?.Invoke(this, EventArgs.Empty);
        _ = SendHelloAsync(stopping.Token);
    }

    public Task<DeviceConfigurationReceivedEventArgs> RequestConfigurationAsync(Peer peer,
                                                                                 CancellationToken cancellationToken = default)
    {
        return SendConfigurationRequestAsync(PacketType.ConfigGet,
            Encoding.UTF8.GetBytes("{\"cmd\":\"get\"}"), peer, cancellationToken);
    }

    public Task<DeviceConfigurationReceivedEventArgs> SetConfigurationAsync(Peer peer, DeviceConfiguration configuration,
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
            soft_mute = configuration.SoftMute,
            mesh_id = configuration.MeshId,
            device_id = configuration.DeviceId,
        });
        return SendConfigurationRequestAsync(PacketType.ConfigSet, Encoding.UTF8.GetBytes(request), peer,
            cancellationToken);
    }

    /// <summary>Offer a cryptographically verified application image to one
    /// idle device. The file host is scoped to that device's current LAN IP.
    /// This returns only after the device verifies the image and announces its
    /// reboot, or when it reports a failure. This keeps the temporary artifact
    /// host alive for the complete transaction.</summary>
    public async Task RequestOtaUpdateAsync(Peer peer, OtaPackage package, OtaArtifactServer server,
                                            CancellationToken cancellationToken = default)
    {
        if (!peer.IsProtocolCompatible || peer.ProtocolVersion != Protocol.Version || !peer.SupportsOta)
            throw new InvalidOperationException($"{peer.Alias} does not support OTA protocol p{Protocol.Version}.");
        if (package.Protocol != Protocol.Version)
            throw new InvalidOperationException($"Package protocol p{package.Protocol} is incompatible with this companion.");
        package.Verify();
        var session = NewSessionId();
        var operation = new OtaOperation();
        if (!otaOperations.TryAdd(session, operation))
            throw new InvalidOperationException("Could not allocate OTA session.");
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                url = server.UrlFor(peer).AbsoluteUri,
                version = package.Version,
                protocol = package.Protocol,
                size = package.Size,
                sha256 = package.Sha256,
                signature = package.Signature,
            });
            if (payload.Length > 320) throw new InvalidDataException("OTA offer exceeds the PTT1 control payload limit.");
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                Diagnostic?.Invoke($"OTA {package.Version} → {peer.Alias} (offer {attempt}/3)");
                await SendToEndpointAsync(PacketType.OtaOffer, session, 0, payload, peer.Endpoint,
                    cancellationToken: cancellationToken);
                var completed = await Task.WhenAny(operation.Admitted.Task,
                    Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
                if (completed == operation.Admitted.Task)
                {
                    await operation.Admitted.Task;
                    break;
                }
                if (operation.Completed.Task.IsCompleted) await operation.Completed.Task;
                if (attempt == 3)
                    throw new TimeoutException($"{peer.Alias} did not acknowledge the OTA offer.");
            }
            await operation.Completed.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
        }
        finally
        {
            otaOperations.TryRemove(session, out _);
        }
    }

    /// <summary>
    /// Configuration changes are idempotent. Retrying their small unicast
    /// datagrams is therefore safe and makes the UI reliable on Wi-Fi without
    /// having to use multicast or retain a configured list of IP addresses.
    /// </summary>
    private async Task<DeviceConfigurationReceivedEventArgs> SendConfigurationRequestAsync(
        PacketType type, byte[] payload, Peer peer, CancellationToken cancellationToken)
    {
        var session = NewSessionId();
        var completion = new TaskCompletionSource<DeviceConfigurationReceivedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!configurationRequests.TryAdd(session, completion))
            throw new InvalidOperationException("Could not allocate configuration request.");
        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                Diagnostic?.Invoke($"Configuration {type} → {peer.Alias} (attempt {attempt}/3)");
                DiagnosticLog.Write($"config send type={type} target={peer.NodeId:x8} session={session:x8} attempt={attempt}");
                await SendToEndpointAsync(type, session, 0, payload, peer.Endpoint,
                    cancellationToken: cancellationToken);
                var elapsed = await Task.WhenAny(completion.Task,
                    Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken));
                if (elapsed == completion.Task) return await completion.Task;
            }
            DiagnosticLog.Write($"config timeout type={type} target={peer.NodeId:x8} session={session:x8}");
            throw new TimeoutException($"{peer.Alias} did not acknowledge the configuration request.");
        }
        finally
        {
            configurationRequests.TryRemove(session, out _);
        }
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

    public Task SendRtpToEndpointAsync(uint session, ushort sequence, uint timestamp, byte[] payload,
                                       IPEndPoint controlDestination,
                                       CancellationToken cancellationToken = default) =>
        SendRtpAsync(session, sequence, timestamp, payload,
            new IPEndPoint(controlDestination.Address, Rtp.Port), cancellationToken);

    public async Task SendRtpToPeersAsync(uint session, ushort sequence, uint timestamp, byte[] payload,
                                          IEnumerable<Peer> destinations,
                                          CancellationToken cancellationToken = default)
    {
        foreach (var peer in destinations)
            await SendRtpAsync(session, sequence, timestamp, payload,
                new IPEndPoint(peer.Endpoint.Address, Rtp.Port), cancellationToken);
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var received = await client.ReceiveAsync(stopping.Token);
                if (!Protocol.TryParse(received.Buffer, Protocol.MeshIdFromText(settings.MeshId), NodeId, out var packet) || packet is null)
                    continue;

                LearnPeer(packet, received.RemoteEndPoint);
                PacketReceived?.Invoke(this, new IntercomPacketReceivedEventArgs(packet, received.RemoteEndPoint));
                if (packet.Type == PacketType.ConfigReply)
                    ParseConfigurationReply(packet, received.RemoteEndPoint);
                else if (packet.Type == PacketType.OtaStatus)
                    ParseOtaStatus(packet, received.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException exception) when (!stopping.IsCancellationRequested)
        {
            Diagnostic?.Invoke($"Network receive stopped: {exception.SocketErrorCode}");
        }
    }

    private async Task MediaReceiveLoopAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var received = await mediaClient.ReceiveAsync(stopping.Token);
                if (!Rtp.TryParse(received.Buffer, out var packet) || packet is null)
                    continue;
                RtpPacketReceived?.Invoke(this, new RtpPacketReceivedEventArgs(packet, received.RemoteEndPoint));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException exception) when (!stopping.IsCancellationRequested)
        {
            Diagnostic?.Invoke($"RTP receive stopped: {exception.SocketErrorCode}");
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
        var announcement = packet.Type == PacketType.Hello
            ? Protocol.ParseHello(packet.Payload)
            : new HelloAnnouncement(known?.Alias ?? $"Device {packet.SenderId:x8}",
                known?.ProtocolVersion, known?.FirmwareVersion ?? "unknown", known?.Capabilities ?? 0,
                known?.HelloFlags ?? 0);
        var peer = new Peer(packet.SenderId, source, announcement.Alias,
            announcement.ProtocolVersion, announcement.FirmwareVersion, announcement.Capabilities,
            announcement.Flags, DateTimeOffset.UtcNow);
        peers[packet.SenderId] = peer;
        if (known is null)
            DiagnosticLog.Write($"peer discovered sender={packet.SenderId:x8} endpoint={source} alias={peer.Alias} firmware={peer.FirmwareVersion} protocol={peer.ProtocolVersion?.ToString() ?? "legacy"}");
        if (known is null || known.Alias != peer.Alias || !known.Endpoint.Equals(peer.Endpoint) ||
            known.ProtocolVersion != peer.ProtocolVersion || known.FirmwareVersion != peer.FirmwareVersion ||
            known.Capabilities != peer.Capabilities || known.HelloFlags != peer.HelloFlags)
            PeersChanged?.Invoke(this, EventArgs.Empty);
    }

    private static IPAddress GetDefaultRouteAddress()
    {
        // UDP Connect only asks the IP stack to select a route; it sends no data.
        // A public address is used solely as a stable route-selection target.
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53));
        return ((IPEndPoint)probe.LocalEndPoint!).Address;
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
                root.TryGetProperty("ring_orientation", out var orientation) ? orientation.GetInt32() : 0,
                root.TryGetProperty("soft_mute", out var softMute) && softMute.GetBoolean(),
                root.TryGetProperty("hw_muted", out var hardwareMute) && hardwareMute.GetBoolean(),
                ReadMeshId(root),
                root.TryGetProperty("device_id", out var deviceId) ? deviceId.GetUInt32() : packet.SenderId);
            var reply = new DeviceConfigurationReceivedEventArgs(packet.SenderId, packet.SessionId, source, config);
            if (configurationRequests.TryGetValue(packet.SessionId, out var completion))
            {
                DiagnosticLog.Write($"config reply sender={packet.SenderId:x8} session={packet.SessionId:x8}");
                completion.TrySetResult(reply);
            }
            else
            {
                DiagnosticLog.Write($"config unsolicited sender={packet.SenderId:x8} session={packet.SessionId:x8}");
            }
        }
        catch (JsonException)
        {
            Diagnostic?.Invoke($"Ignored invalid configuration reply from {source.Address}.");
        }
    }

    private static string ReadMeshId(JsonElement root)
    {
        if (!root.TryGetProperty("mesh_id", out var meshId)) return "MESH";
        if (meshId.ValueKind == JsonValueKind.String)
            return Protocol.IsValidMeshId(meshId.GetString()) ? meshId.GetString()! : "MESH";
        if (meshId.ValueKind == JsonValueKind.Number && meshId.TryGetUInt32(out var numeric))
            return Protocol.MeshIdToText(numeric);
        return "MESH";
    }

    private void ParseOtaStatus(IntercomPacket packet, IPEndPoint source)
    {
        try
        {
            using var json = JsonDocument.Parse(packet.Payload);
            var root = json.RootElement;
            var status = new OtaStatus(
                root.TryGetProperty("state", out var state) ? state.GetString() ?? "unknown" : "unknown",
                root.TryGetProperty("progress", out var progress) ? progress.GetInt32() : 0,
                root.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "",
                root.TryGetProperty("version", out var version) ? version.GetString() ?? "unknown" : "unknown");
            DiagnosticLog.Write($"ota status sender={packet.SenderId:x8} session={packet.SessionId:x8} state={status.State} progress={status.Progress}");
            OtaStatusReceived?.Invoke(this, new OtaStatusReceivedEventArgs(packet.SenderId, packet.SessionId, source, status));
            if (otaOperations.TryGetValue(packet.SessionId, out var operation))
            {
                if (status.State is "rejected" or "failed")
                {
                    var exception = new InvalidOperationException(status.Message);
                    operation.Admitted.TrySetException(exception);
                    operation.Completed.TrySetException(exception);
                }
                else if (status.State is "accepted" or "downloading")
                    operation.Admitted.TrySetResult(status);
                else if (status.State == "rebooting")
                {
                    operation.Admitted.TrySetResult(status);
                    operation.Completed.TrySetResult(status);
                }
            }
        }
        catch (JsonException)
        {
            Diagnostic?.Invoke($"Ignored invalid OTA status from {source.Address}.");
        }
    }

    private async Task SendHelloAsync(CancellationToken cancellationToken)
    {
        try
        {
            var payload = Protocol.BuildHelloPayload(Alias, OwnFirmwareVersion());
            await SendAsync(PacketType.Hello, 0, 0, payload,
                new IPEndPoint(MulticastGroup, Protocol.Port), 0, cancellationToken);
            // Some consumer access points suppress multicast traffic between
            // Wi-Fi clients. The link-local broadcast carries the same small
            // HELLO only as discovery fallback; media remains unicast.
            await SendAsync(PacketType.Hello, 0, 0, payload,
                new IPEndPoint(IPAddress.Broadcast, Protocol.Port), 0, cancellationToken);
            // Once a peer has answered either discovery beacon, its unicast
            // endpoint is authoritative. Refresh it directly as well: a few
            // consumer APs intermittently suppress Wi-Fi multicast/broadcast
            // between clients, but normal unicast (used by media/config) is
            // reliable. A peer still expires after PeerLifetime if it stops
            // answering these probes.
            var peersToRefresh = SnapshotActivePeers();
            foreach (var peer in peersToRefresh)
                await SendAsync(PacketType.Hello, 0, 0, payload, peer.Endpoint, 0, cancellationToken);

            // Last-resort peerless discovery for APs which forward neither
            // multicast nor subnet broadcasts between Wi-Fi clients. The
            // local /24 is derived at runtime from the active default route;
            // no device addresses are stored or configured. At 254 compact
            // HELLO datagrams every 12 seconds it is negligible on this LAN.
            var now = DateTimeOffset.UtcNow;
            if (now >= nextSubnetProbeAt)
            {
                nextSubnetProbeAt = now.AddSeconds(12);
                foreach (var endpoint in LocalSubnetProbeEndpoints())
                    await SendAsync(PacketType.Hello, 0, 0, payload, endpoint, 0, cancellationToken);
                DiagnosticLog.Write("discovery unicast subnet probe sent");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException exception)
        {
            DiagnosticLog.Write($"discovery hello failed: {exception.SocketErrorCode}");
            Diagnostic?.Invoke($"Discovery send failed: {exception.SocketErrorCode}");
        }
    }

    private static string OwnFirmwareVersion() =>
        typeof(IntercomNode).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private IEnumerable<IPEndPoint> LocalSubnetProbeEndpoints()
    {
        var bytes = discoveryInterface.GetAddressBytes();
        // Wi-Fi intercom deployment uses the standard consumer-router /24.
        // Restricting the sweep to it prevents excessive probing on an office
        // /16 while still covering all DHCP leases on the active LAN.
        for (var host = 1; host < 255; host++)
        {
            if (host == bytes[3]) continue;
            yield return new IPEndPoint(new IPAddress([bytes[0], bytes[1], bytes[2], (byte)host]), Protocol.Port);
        }
    }

    private async Task SendAsync(PacketType type, uint session, uint sequence, byte[] payload,
                                 IPEndPoint destination, byte flags,
                                 CancellationToken cancellationToken)
    {
        var datagram = Protocol.Pack(Protocol.MeshIdFromText(settings.MeshId), NodeId, type, session, sequence, TimestampMs(), payload, flags);
        await sendGate.WaitAsync(cancellationToken);
        try { await client.SendAsync(datagram, destination, cancellationToken); }
        finally { sendGate.Release(); }
    }

    private async Task SendRtpAsync(uint session, ushort sequence, uint timestamp, byte[] payload,
                                    IPEndPoint destination, CancellationToken cancellationToken)
    {
        var datagram = Rtp.Pack(session, sequence, timestamp, payload);
        await mediaSendGate.WaitAsync(cancellationToken);
        try { await mediaClient.SendAsync(datagram, destination, cancellationToken); }
        finally { mediaSendGate.Release(); }
    }

    // WMM maps DSCP precedence 4 to the video access category, which keeps
    // voice media ahead of best-effort traffic without abusing voice AC.
    private static void SetVideoPriority(Socket socket)
    {
        try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0x80); }
        catch (SocketException) { }
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
        var tasks = new[] { receiveTask, mediaReceiveTask, helloTask, expiryTask }.OfType<Task>();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
        sendGate.Dispose();
        mediaSendGate.Dispose();
        stopping.Dispose();
    }

    /// <summary>Non-blocking shutdown for the WinForms UI thread.</summary>
    public void Stop()
    {
        if (!stopping.IsCancellationRequested) stopping.Cancel();
        client.Dispose();
        mediaClient.Dispose();
    }
}

internal sealed class DeviceConfigurationReceivedEventArgs(uint nodeId, uint sessionId, IPEndPoint endpoint,
                                                            DeviceConfiguration configuration) : EventArgs
{
    public uint NodeId { get; } = nodeId;
    public uint SessionId { get; } = sessionId;
    public IPEndPoint Endpoint { get; } = endpoint;
    public DeviceConfiguration Configuration { get; } = configuration;
}

internal sealed class IntercomPacketReceivedEventArgs(IntercomPacket packet, IPEndPoint endpoint) : EventArgs
{
    public IntercomPacket Packet { get; } = packet;
    public IPEndPoint Endpoint { get; } = endpoint;
}

internal sealed class RtpPacketReceivedEventArgs(RtpPacket packet, IPEndPoint endpoint) : EventArgs
{
    public RtpPacket Packet { get; } = packet;
    public IPEndPoint Endpoint { get; } = endpoint;
}

internal sealed record OtaStatus(string State, int Progress, string Message, string Version);

internal sealed class OtaOperation
{
    public TaskCompletionSource<OtaStatus> Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<OtaStatus> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class OtaStatusReceivedEventArgs(uint nodeId, uint sessionId, IPEndPoint endpoint,
                                                  OtaStatus status) : EventArgs
{
    public uint NodeId { get; } = nodeId;
    public uint SessionId { get; } = sessionId;
    public IPEndPoint Endpoint { get; } = endpoint;
    public OtaStatus Status { get; } = status;
}
