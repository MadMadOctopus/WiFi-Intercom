using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using IntercomCompanion.Core;

// Substitute hardware/IO boundaries only; production ReceiveSession, codecs,
// discovery payloads, peer model and selection policy are linked by the project.
namespace NAudio.Wave
{
    internal sealed class WaveFormat(int rate, int bits, int channels) { public int Rate => rate + bits + channels; }
    internal sealed class WaveFileWriter(string path, WaveFormat format) : IDisposable
    {
        public void Write(byte[] buffer, int offset, int count) { _ = path; _ = format; }
        public void Dispose() { }
    }
}
namespace IntercomCompanion.Audio
{
    internal sealed class AudioEngine
    {
        public const int SampleRate = 16000;
        public ChannelReader<short[]> CapturedFrames => frames.Reader;
        private readonly Channel<short[]> frames = Channel.CreateUnbounded<short[]>();
        public int Played { get; private set; }
        public void StartCapture() { }
        public void ClearPlayback() { Played = 0; }
        public void EnqueuePlayback(short[] pcm) { Played++; }
    }
}
namespace IntercomCompanion.Core
{
    internal static class DiagnosticLog { public static void Write(string message) { } }
    internal sealed class IntercomPacketReceivedEventArgs(IntercomPacket packet, IPEndPoint endpoint) : EventArgs
    { public IntercomPacket Packet => packet; public IPEndPoint Endpoint => endpoint; }
    internal sealed class RtpPacketReceivedEventArgs(RtpPacket packet, IPEndPoint endpoint) : EventArgs
    { public RtpPacket Packet => packet; public IPEndPoint Endpoint => endpoint; }
    internal sealed class IntercomNode(uint id, SimNetwork network)
    {
        private static int nextSession = 100;
        public uint NodeId => id;
        public string BroadcastTargetCode { get; set; } = "MESH";
        public IReadOnlyList<string> JoinedGroupCodes => ["MESH"];
        public Peer Self => new(id, new IPEndPoint(IPAddress.Parse($"127.0.0.{id}"), Protocol.Port), $"Node {id}", Protocol.Version, "test", 0, 0, DateTimeOffset.UtcNow);
        public IReadOnlyCollection<Peer> Peers => network.Nodes.Where(n => n != this).Select(n => n.Self).ToArray();
        public IReadOnlyList<Peer> SnapshotGroupPeers(string code) => Peers.Where(p => p.GroupCode == code).ToArray();
        public IReadOnlyList<Peer> SnapshotJoinedPeers() => Peers.ToArray();
        public static uint NewSessionId() => (uint)Interlocked.Increment(ref nextSession);
        public event EventHandler<IntercomPacketReceivedEventArgs>? PacketReceived;
        public event EventHandler<RtpPacketReceivedEventArgs>? RtpPacketReceived;
        public void Deliver(IntercomPacket packet, IPEndPoint source) => PacketReceived?.Invoke(this, new(packet, source));
        public void Media(RtpPacket packet, IPEndPoint source) => RtpPacketReceived?.Invoke(this, new(packet, source));
        public Task SendToEndpointAsync(PacketType type, uint session, uint sequence, byte[] payload, IPEndPoint destination,
            string groupCode, byte flags = 0, CancellationToken cancellationToken = default)
        {
            network.Pending.Enqueue((this, destination, Protocol.Pack(Protocol.MeshIdFromText(groupCode), id, type, session, sequence, 0, payload, flags)));
            return Task.CompletedTask;
        }
        public async Task SendToPeersAsync(PacketType type, uint session, uint sequence, byte[] payload, IEnumerable<Peer> peers,
            byte flags = 0, CancellationToken cancellationToken = default)
        { foreach (var peer in peers) await SendToEndpointAsync(type, session, sequence, payload, peer.Endpoint, peer.GroupCode, flags, cancellationToken); }
        public Task SendRtpToEndpointAsync(uint session, ushort sequence, uint timestamp, byte[] payload, IPEndPoint endpoint, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendRtpToPeersAsync(uint session, ushort sequence, uint timestamp, byte[] payload, IEnumerable<Peer> peers, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
internal sealed class SimNetwork : IAsyncDisposable
{
    public List<IntercomNode> Nodes { get; } = [];
    public List<ReceiveSession> Sessions { get; } = [];
    public ConcurrentQueue<(IntercomNode Source, IPEndPoint Destination, byte[] Bytes)> Pending { get; } = new();
    public List<IntercomPacket> Sent { get; } = [];
    public bool DropAccept { get; set; }
    public SimNetwork(int count)
    {
        for (uint id = 1; id <= count; id++) Nodes.Add(new(id, this));
        foreach (var node in Nodes) { var s = new ReceiveSession(node, new IntercomCompanion.Audio.AudioEngine()); Sessions.Add(s); s.Start(); }
    }
    public void Pump()
    {
        while (Pending.TryDequeue(out var item))
        {
            var target = Nodes.Single(n => n.Self.Endpoint.Equals(item.Destination));
            Check.That(Protocol.TryParse(item.Bytes, target.NodeId, out var packet), "wire parse");
            Sent.Add(packet!);
            if (!(DropAccept && packet!.Type == PacketType.Accept)) target.Deliver(packet!, item.Source.Self.Endpoint);
        }
    }
    public async Task Run(int milliseconds = 180)
    {
        var until = DateTimeOffset.UtcNow.AddMilliseconds(milliseconds);
        do { Pump(); await Task.Delay(5); } while (DateTimeOffset.UtcNow < until);
        Pump();
    }
    public async ValueTask DisposeAsync() { foreach (var session in Sessions) await session.DisposeAsync(); }
}
internal static class Check
{
    public static int Count;
    public static void That(bool condition, string message) { Count++; if (!condition) throw new Exception(message); }
}
