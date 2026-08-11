using System.Net;
using System.Threading.Channels;
using IntercomCompanion.Audio;
using NAudio.Wave;

namespace IntercomCompanion.Core;

internal enum IntercomState { Idle, Receiving }
internal sealed record ReceiveStatistics(long AudioPackets, long DecodedFrames, long PlayedFrames, long ConcealedFrames);

/// <summary>
/// Receive-only half of the floor state machine. TX is added separately once
/// this path is proven with a long device broadcast.
/// </summary>
internal sealed class ReceiveSession : IAsyncDisposable
{
    private const int FrameMs = 20;
    private const int ReleaseMs = 750;
    private const int EndDrainFrames = 8;

    private readonly IntercomNode node;
    private readonly AudioEngine audio;
    private readonly JitterBuffer jitter = new();
    private readonly Channel<IntercomPacketReceivedEventArgs> audioPackets = Channel.CreateBounded<IntercomPacketReceivedEventArgs>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    private readonly object gate = new();
    private readonly CancellationTokenSource stopping = new();
    private Task? decodeTask;
    private Task? playbackTask;
    private IntercomState state;
    private uint senderId;
    private uint sessionId;
    private DateTimeOffset lastAudioAt;
    private bool ending;
    private int drainFrames;
    private WaveFileWriter? recording;
    private long audioPacketCount;
    private long decodedFrameCount;
    private long playedFrameCount;
    private long concealedFrameCount;

    public ReceiveSession(IntercomNode node, AudioEngine audio)
    {
        this.node = node;
        this.audio = audio;
        node.PacketReceived += OnPacketReceived;
    }

    public IntercomState State { get { lock (gate) return state; } }
    public Peer? LastTalker { get; private set; }
    public ReceiveStatistics Statistics => new(Interlocked.Read(ref audioPacketCount),
        Interlocked.Read(ref decodedFrameCount), Interlocked.Read(ref playedFrameCount),
        Interlocked.Read(ref concealedFrameCount));
    public event Action<IntercomState>? StateChanged;
    public event Action<string>? Diagnostic;

    public void Start()
    {
        decodeTask = Task.Run(DecodeLoopAsync);
        playbackTask = Task.Run(PlaybackLoopAsync);
    }

    private void OnPacketReceived(object? sender, IntercomPacketReceivedEventArgs eventArgs)
    {
        var packet = eventArgs.Packet;
        if (packet.Type == PacketType.Claim)
        {
            lock (gate)
            {
                if (state == IntercomState.Idle) BeginLocked(packet, eventArgs.Endpoint);
            }
            return;
        }
        if (packet.Type == PacketType.Audio)
        {
            // Do not decode under the UDP receive loop. A congested app should
            // drop only stale audio, never discovery/control packets.
            Interlocked.Increment(ref audioPacketCount);
            audioPackets.Writer.TryWrite(eventArgs);
            return;
        }
        lock (gate)
        {
            if (state != IntercomState.Receiving || packet.SenderId != senderId || packet.SessionId != sessionId)
                return;
            if (packet.Type == PacketType.Heartbeat) lastAudioAt = DateTimeOffset.UtcNow;
            if (packet.Type == PacketType.End && !ending)
            {
                ending = true;
                drainFrames = EndDrainFrames;
            }
        }
    }

    private async Task DecodeLoopAsync()
    {
        try
        {
            await foreach (var eventArgs in audioPackets.Reader.ReadAllAsync(stopping.Token))
            {
                var packet = eventArgs.Packet;
                if (!ImaAdpcm.TryDecode(packet.Payload, out var pcm) || pcm is null) continue;
                Interlocked.Increment(ref decodedFrameCount);
                lock (gate)
                {
                    if (state == IntercomState.Idle) BeginLocked(packet, eventArgs.Endpoint);
                    if (state != IntercomState.Receiving || packet.SenderId != senderId || packet.SessionId != sessionId)
                        continue;
                    lastAudioAt = DateTimeOffset.UtcNow;
                    LastTalker = new Peer(packet.SenderId, eventArgs.Endpoint,
                        node.Peers.FirstOrDefault(peer => peer.NodeId == packet.SenderId)?.Alias ?? $"Device {packet.SenderId:x8}",
                        DateTimeOffset.UtcNow);
                    jitter.Push(packet.Sequence, pcm);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task PlaybackLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(FrameMs));
            while (await timer.WaitForNextTickAsync(stopping.Token))
            {
                short[]? pcm = null;
                var realAudio = false;
                var finish = false;
                lock (gate)
                {
                    if (state != IntercomState.Receiving) continue;
                    if (DateTimeOffset.UtcNow - lastAudioAt > TimeSpan.FromMilliseconds(ReleaseMs)) finish = true;
                    else if (jitter.TryPop(out pcm, out realAudio))
                    {
                        if (ending && --drainFrames <= 0) finish = true;
                    }
                    if (realAudio && pcm is not null) WriteRecordingLocked(pcm);
                    if (realAudio) Interlocked.Increment(ref playedFrameCount);
                    else if (pcm is not null) Interlocked.Increment(ref concealedFrameCount);
                    if (finish) FinishLocked();
                }
                if (pcm is not null && !finish) audio.EnqueuePlayback(pcm);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void BeginLocked(IntercomPacket packet, IPEndPoint endpoint)
    {
        senderId = packet.SenderId;
        sessionId = packet.SessionId;
        lastAudioAt = DateTimeOffset.UtcNow;
        ending = false;
        drainFrames = 0;
        jitter.Reset();
        audio.ClearPlayback();
        Interlocked.Exchange(ref audioPacketCount, 0);
        Interlocked.Exchange(ref decodedFrameCount, 0);
        Interlocked.Exchange(ref playedFrameCount, 0);
        Interlocked.Exchange(ref concealedFrameCount, 0);
        Directory.CreateDirectory(CompanionSettings.RecordingsDirectory);
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{senderId:x8}-{sessionId:x8}.wav";
        recording = new WaveFileWriter(Path.Combine(CompanionSettings.RecordingsDirectory, name),
            new WaveFormat(AudioEngine.SampleRate, 16, 1));
        state = IntercomState.Receiving;
        DiagnosticLog.Write($"rx begin sender={senderId:x8} session={sessionId:x8}");
        StateChanged?.Invoke(state);
        Diagnostic?.Invoke($"Receiving {senderId:x8}.");
    }

    private void WriteRecordingLocked(short[] pcm)
    {
        if (recording is null) return;
        var bytes = new byte[pcm.Length * sizeof(short)];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        recording.Write(bytes, 0, bytes.Length);
    }

    private void FinishLocked()
    {
        var stats = Statistics;
        DiagnosticLog.Write($"rx finish sender={senderId:x8} session={sessionId:x8} " +
                            $"udp={stats.AudioPackets} decoded={stats.DecodedFrames} played={stats.PlayedFrames} plc={stats.ConcealedFrames}");
        recording?.Dispose();
        recording = null;
        jitter.Reset();
        audio.ClearPlayback();
        ending = false;
        state = IntercomState.Idle;
        StateChanged?.Invoke(state);
        Diagnostic?.Invoke("Receive session finished.");
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        var tasks = new[] { decodeTask, playbackTask }.OfType<Task>();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
        lock (gate) { recording?.Dispose(); recording = null; }
        stopping.Dispose();
    }

    /// <summary>Non-blocking shutdown for the WinForms UI thread.</summary>
    public void Stop()
    {
        node.PacketReceived -= OnPacketReceived;
        if (!stopping.IsCancellationRequested) stopping.Cancel();
        audioPackets.Writer.TryComplete();
        lock (gate)
        {
            if (state == IntercomState.Receiving)
            {
                var stats = Statistics;
                DiagnosticLog.Write($"rx stopped sender={senderId:x8} session={sessionId:x8} " +
                                    $"udp={stats.AudioPackets} decoded={stats.DecodedFrames} played={stats.PlayedFrames} plc={stats.ConcealedFrames}");
            }
            recording?.Dispose();
            recording = null;
        }
    }
}
