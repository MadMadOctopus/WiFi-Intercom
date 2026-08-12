using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using IntercomCompanion.Audio;
using NAudio.Wave;

namespace IntercomCompanion.Core;

internal enum IntercomState { Idle, Claiming, Talking, Receiving, WaitingForFloor }
internal enum PttKind { Broadcast, Reply, Selected }
internal sealed record ReceiveStatistics(long AudioPackets, long DecodedFrames, long PlayedFrames,
                                         long ConcealedFrames, long SequenceGaps);

/// <summary>
/// Full PC floor/session state machine. Audio callbacks only feed channels;
/// floor control, encoding and UDP sends run on background tasks.
/// </summary>
internal sealed class ReceiveSession : IAsyncDisposable
{
    private const int FrameMs = 20;
    private const int ReleaseMs = 750;
    // Allow the full 400 ms prebuffer plus the reorder window to drain after
    // END, otherwise the increased playout delay would cut the message tail.
    private const int EndDrainFrames = 32;
    private const int ClaimCount = 3;
    private const int ClaimIntervalMs = 30;
    private const int PreAudioDelayMs = 100;
    private const int BusyBufferFrames = 25;

    private readonly IntercomNode node;
    private readonly AudioEngine audio;
    private readonly JitterBuffer jitter = new();
    private readonly ImaAdpcm encoder = new();
    private readonly Channel<RtpPacketReceivedEventArgs> audioPackets = Channel.CreateBounded<RtpPacketReceivedEventArgs>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    private readonly object gate = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly Queue<short[]> heldFrames = [];
    private Task? decodeTask;
    private Task? playbackTask;
    private Task? captureTask;
    private Task? heartbeatTask;
    private CancellationTokenSource? transmitStopping;
    private IntercomState state;

    // receive session
    private uint senderId;
    private uint sessionId;
    private DateTimeOffset lastAudioAt;
    private bool ending;
    private int drainFrames;
    private WaveFileWriter? recording;

    // transmit session
    private bool pttHeld;
    private uint transmitSession;
    private uint transmitSequence;
    private ushort transmitRtpSequence;
    private uint transmitRtpTimestamp;
    private bool transmitDirected;
    private Peer? transmitTarget;
    private IReadOnlyList<Peer> transmitPeers = [];
    private PttKind heldKind;
    private Peer? heldTarget;
    private DateTimeOffset heldSince;

    // diagnostics
    private long audioPacketCount;
    private long decodedFrameCount;
    private long playedFrameCount;
    private long concealedFrameCount;
    private long sequenceGapCount;
    private ushort previousSequence;
    private bool havePreviousSequence;

    public ReceiveSession(IntercomNode node, AudioEngine audio)
    {
        this.node = node;
        this.audio = audio;
        node.PacketReceived += OnPacketReceived;
        node.RtpPacketReceived += OnRtpPacketReceived;
    }

    public IntercomState State { get { lock (gate) return state; } }
    public Peer? LastTalker { get; private set; }
    public ReceiveStatistics Statistics => new(Interlocked.Read(ref audioPacketCount),
        Interlocked.Read(ref decodedFrameCount), Interlocked.Read(ref playedFrameCount),
        Interlocked.Read(ref concealedFrameCount), Interlocked.Read(ref sequenceGapCount));
    public event Action<IntercomState>? StateChanged;
    public event Action<string>? Diagnostic;

    public void Start()
    {
        audio.StartCapture();
        decodeTask = Task.Run(DecodeLoopAsync);
        playbackTask = Task.Run(PlaybackLoopAsync);
        captureTask = Task.Run(CaptureLoopAsync);
    }

    public void PressBroadcast() => Press(PttKind.Broadcast, null);
    public void PressReply() => Press(PttKind.Reply, LastTalker);
    public void PressSelected(Peer peer) => Press(PttKind.Selected, peer);

    private void Press(PttKind kind, Peer? target)
    {
        lock (gate)
        {
            if (pttHeld) return;
            if (kind == PttKind.Reply && target is null)
            {
                Diagnostic?.Invoke("Reply unavailable: no previous talker.");
                return;
            }
            if (kind == PttKind.Selected && target is null)
            {
                Diagnostic?.Invoke("Select a device before using targeted PTT.");
                return;
            }
            pttHeld = true;
            if (state == IntercomState.Idle)
            {
                StartTransmitLocked(kind, target);
                return;
            }
            if (state == IntercomState.Receiving)
            {
                heldKind = kind;
                heldTarget = target;
                heldSince = DateTimeOffset.UtcNow;
                heldFrames.Clear();
                SetStateLocked(IntercomState.WaitingForFloor);
                Diagnostic?.Invoke("Floor occupied: retaining up to 500 ms.");
            }
        }
    }

    public void ReleasePtt()
    {
        uint endSession = 0;
        IReadOnlyList<Peer>? endPeers = null;
        byte endFlags = 0;
        lock (gate)
        {
            if (!pttHeld) return;
            pttHeld = false;
            if (state == IntercomState.WaitingForFloor)
            {
                heldFrames.Clear();
                SetStateLocked(IntercomState.Receiving);
                return;
            }
            if (state is not (IntercomState.Claiming or IntercomState.Talking)) return;
            endSession = transmitSession;
            endPeers = transmitPeers;
            endFlags = transmitDirected ? Protocol.DirectedFlag : (byte)0;
            CancelTransmitLocked();
            SetStateLocked(IntercomState.Idle);
        }
        _ = SendEndAsync(endSession, endPeers, endFlags);
    }

    private void StartTransmitLocked(PttKind kind, Peer? target)
    {
        transmitPeers = node.SnapshotActivePeers();
        if (target is not null && transmitPeers.All(peer => peer.NodeId != target.NodeId))
            transmitPeers = transmitPeers.Append(target).ToArray();
        transmitDirected = kind is not PttKind.Broadcast;
        transmitTarget = target;
        transmitSession = IntercomNode.NewSessionId();
        transmitSequence = 0;
        transmitRtpSequence = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
        transmitRtpTimestamp = unchecked((uint)Random.Shared.NextInt64(uint.MaxValue + 1L));
        encoder.Reset();
        transmitStopping?.Cancel();
        transmitStopping?.Dispose();
        transmitStopping = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        SetStateLocked(IntercomState.Claiming);
        Diagnostic?.Invoke($"Claiming floor for {(transmitDirected ? "direct" : "broadcast")} message.");
        _ = Task.Run(() => ClaimSequenceAsync(transmitSession, transmitStopping.Token));
    }

    private async Task ClaimSequenceAsync(uint claimSession, CancellationToken cancellationToken)
    {
        try
        {
            for (var count = 0; count < ClaimCount; count++)
            {
                IReadOnlyList<Peer> peers;
                byte flags;
                lock (gate)
                {
                    if (state != IntercomState.Claiming || transmitSession != claimSession) return;
                    peers = transmitPeers;
                    flags = transmitDirected ? Protocol.DirectedFlag : (byte)0;
                }
                await node.SendToPeersAsync(PacketType.Claim, claimSession, 0, [], peers, flags, cancellationToken);
                await Task.Delay(ClaimIntervalMs, cancellationToken);
            }
            await Task.Delay(PreAudioDelayMs - ClaimCount * ClaimIntervalMs, cancellationToken);
            lock (gate)
            {
                if (state != IntercomState.Claiming || transmitSession != claimSession) return;
                SetStateLocked(IntercomState.Talking);
                heartbeatTask = Task.Run(() => HeartbeatLoopAsync(claimSession, cancellationToken));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException exception) { Diagnostic?.Invoke($"Claim send failed: {exception.SocketErrorCode}"); }
    }

    private async Task HeartbeatLoopAsync(uint heartbeatSession, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                IReadOnlyList<Peer> peers;
                byte flags;
                lock (gate)
                {
                    if (state != IntercomState.Talking || transmitSession != heartbeatSession) return;
                    if (!transmitDirected) continue;
                    peers = transmitPeers;
                    flags = Protocol.DirectedFlag;
                }
                await node.SendToPeersAsync(PacketType.Heartbeat, heartbeatSession, 0, [], peers, flags, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task CaptureLoopAsync()
    {
        try
        {
            await foreach (var liveFrame in audio.CapturedFrames.ReadAllAsync(stopping.Token))
            {
                uint outgoingSession = 0, outgoingTimestamp = 0;
                ushort outgoingSequence = 0;
                byte[]? payload = null;
                IReadOnlyList<Peer>? peers = null;
                Peer? target = null;
                lock (gate)
                {
                    if (state == IntercomState.WaitingForFloor)
                    {
                        if (DateTimeOffset.UtcNow - heldSince >= TimeSpan.FromMilliseconds(BusyBufferFrames * FrameMs))
                        {
                            heldFrames.Clear();
                            SetStateLocked(IntercomState.Receiving);
                            Diagnostic?.Invoke("Floor stayed occupied for 500 ms; PTT cancelled.");
                        }
                        else if (heldFrames.Count < BusyBufferFrames) heldFrames.Enqueue(liveFrame);
                    }
                    if (state != IntercomState.Talking) continue;
                    var frame = heldFrames.Count > 0 ? heldFrames.Dequeue() : liveFrame;
                    payload = encoder.Encode(frame);
                    outgoingSession = transmitSession;
                    outgoingSequence = transmitRtpSequence++;
                    outgoingTimestamp = transmitRtpTimestamp;
                    transmitRtpTimestamp += Rtp.TimestampStep;
                    transmitSequence++;
                    peers = transmitPeers;
                    target = transmitTarget;
                }
                if (payload is null) continue;
                try
                {
                    if (target is not null)
                        await node.SendRtpToEndpointAsync(outgoingSession, outgoingSequence, outgoingTimestamp,
                            payload, target.Endpoint, stopping.Token);
                    else if (peers is not null)
                        await node.SendRtpToPeersAsync(outgoingSession, outgoingSequence, outgoingTimestamp,
                            payload, peers, stopping.Token);
                }
                catch (OperationCanceledException) { }
                catch (SocketException exception) { Diagnostic?.Invoke($"Audio send failed: {exception.SocketErrorCode}"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SendEndAsync(uint endSession, IReadOnlyList<Peer> peers, byte flags)
    {
        try
        {
            for (var count = 0; count < 3; count++)
            {
                await node.SendToPeersAsync(PacketType.End, endSession, transmitSequence, [], peers, flags, stopping.Token);
                await Task.Delay(5, stopping.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnPacketReceived(object? sender, IntercomPacketReceivedEventArgs eventArgs)
    {
        var packet = eventArgs.Packet;
        if (packet.Type == PacketType.Claim)
        {
            var sendBusy = false;
            lock (gate)
            {
                if (state is IntercomState.Claiming or IntercomState.Talking)
                {
                    if (RemoteWins(packet)) { CancelTransmitLocked(); BeginReceivingLocked(packet, eventArgs.Endpoint); }
                    else sendBusy = true;
                }
                else if (state == IntercomState.Idle) BeginReceivingLocked(packet, eventArgs.Endpoint);
                else if (packet.SessionId != sessionId) sendBusy = true;
            }
            if (sendBusy)
                _ = node.SendToEndpointAsync(PacketType.Busy, sessionId, 0, [], eventArgs.Endpoint);
            return;
        }
        lock (gate)
        {
            if (packet.Type == PacketType.Busy && state == IntercomState.Claiming)
            {
                CancelTransmitLocked();
                SetStateLocked(IntercomState.Idle);
                Diagnostic?.Invoke("Another peer owns the floor.");
                return;
            }
            if (state is not (IntercomState.Receiving or IntercomState.WaitingForFloor) ||
                packet.SenderId != senderId || packet.SessionId != sessionId) return;
            if (packet.Type == PacketType.Heartbeat) lastAudioAt = DateTimeOffset.UtcNow;
            if (packet.Type == PacketType.End && !ending) { ending = true; drainFrames = EndDrainFrames; }
        }
    }

    private void OnRtpPacketReceived(object? sender, RtpPacketReceivedEventArgs eventArgs)
    {
        lock (gate)
        {
            if (state is not (IntercomState.Receiving or IntercomState.WaitingForFloor) ||
                eventArgs.Packet.Ssrc != sessionId) return;
            Interlocked.Increment(ref audioPacketCount);
            audioPackets.Writer.TryWrite(eventArgs);
        }
    }

    private bool RemoteWins(IntercomPacket packet) =>
        packet.SessionId < transmitSession || (packet.SessionId == transmitSession && packet.SenderId < node.NodeId);

    private async Task DecodeLoopAsync()
    {
        try
        {
            await foreach (var eventArgs in audioPackets.Reader.ReadAllAsync(stopping.Token))
            {
                var packet = eventArgs.Packet;
                short[] pcm;
                if (!ImaAdpcm.TryDecode(packet.Payload, out pcm) || pcm is null) continue;
                Interlocked.Increment(ref decodedFrameCount);
                lock (gate)
                {
                    if (state is not (IntercomState.Receiving or IntercomState.WaitingForFloor) ||
                        packet.Ssrc != sessionId) continue;
                    if (havePreviousSequence && packet.Sequence != unchecked((ushort)(previousSequence + 1)))
                        Interlocked.Increment(ref sequenceGapCount);
                    previousSequence = packet.Sequence;
                    havePreviousSequence = true;
                    lastAudioAt = DateTimeOffset.UtcNow;
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
                    if (state is not (IntercomState.Receiving or IntercomState.WaitingForFloor)) continue;
                    if (state == IntercomState.WaitingForFloor && DateTimeOffset.UtcNow - heldSince >= TimeSpan.FromMilliseconds(BusyBufferFrames * FrameMs))
                    {
                        heldFrames.Clear();
                        SetStateLocked(IntercomState.Receiving);
                        Diagnostic?.Invoke("Floor stayed occupied for 500 ms; PTT cancelled.");
                    }
                    if (DateTimeOffset.UtcNow - lastAudioAt > TimeSpan.FromMilliseconds(ReleaseMs)) finish = true;
                    else if (jitter.TryPop(out pcm, out realAudio) && ending && --drainFrames <= 0) finish = true;
                    if (realAudio && pcm is not null) WriteRecordingLocked(pcm);
                    if (realAudio) Interlocked.Increment(ref playedFrameCount);
                    else if (pcm is not null) Interlocked.Increment(ref concealedFrameCount);
                    if (finish) FinishReceivingLocked();
                }
                if (pcm is not null && !finish) audio.EnqueuePlayback(pcm);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void BeginReceivingLocked(IntercomPacket packet, IPEndPoint endpoint)
    {
        CancelTransmitLocked();
        senderId = packet.SenderId;
        sessionId = packet.SessionId;
        lastAudioAt = DateTimeOffset.UtcNow;
        ending = false;
        drainFrames = 0;
        jitter.Reset();
        audio.ClearPlayback();
        ResetStatisticsLocked();
        Directory.CreateDirectory(CompanionSettings.RecordingsDirectory);
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{senderId:x8}-{sessionId:x8}.wav";
        recording?.Dispose();
        recording = new WaveFileWriter(Path.Combine(CompanionSettings.RecordingsDirectory, name), new WaveFormat(AudioEngine.SampleRate, 16, 1));
        SetStateLocked(IntercomState.Receiving);
        DiagnosticLog.Write($"rx begin sender={senderId:x8} session={sessionId:x8}");
        Diagnostic?.Invoke($"Receiving {senderId:x8}.");
    }

    private void FinishReceivingLocked()
    {
        var stats = Statistics;
        DiagnosticLog.Write($"rx finish sender={senderId:x8} session={sessionId:x8} udp={stats.AudioPackets} decoded={stats.DecodedFrames} played={stats.PlayedFrames} plc={stats.ConcealedFrames} gaps={stats.SequenceGaps}");
        recording?.Dispose();
        recording = null;
        jitter.Reset();
        // Do not clear the Windows output here: with a jitter-buffer delay,
        // it can still contain the final audio frames. The next receive session
        // clears it before it begins, so stale sound cannot cross sessions.
        ending = false;
        if (state == IntercomState.WaitingForFloor && pttHeld)
            StartTransmitLocked(heldKind, heldTarget);
        else SetStateLocked(IntercomState.Idle);
    }

    private void WriteRecordingLocked(short[] pcm)
    {
        if (recording is null) return;
        var bytes = new byte[pcm.Length * sizeof(short)];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        recording.Write(bytes, 0, bytes.Length);
    }

    private void ResetStatisticsLocked()
    {
        Interlocked.Exchange(ref audioPacketCount, 0);
        Interlocked.Exchange(ref decodedFrameCount, 0);
        Interlocked.Exchange(ref playedFrameCount, 0);
        Interlocked.Exchange(ref concealedFrameCount, 0);
        Interlocked.Exchange(ref sequenceGapCount, 0);
        havePreviousSequence = false;
    }

    private void CancelTransmitLocked()
    {
        transmitStopping?.Cancel();
        transmitStopping?.Dispose();
        transmitStopping = null;
        heldFrames.Clear();
    }

    private void SetStateLocked(IntercomState next)
    {
        if (state == next) return;
        state = next;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        var tasks = new[] { decodeTask, playbackTask, captureTask, heartbeatTask }.OfType<Task>();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
        stopping.Dispose();
    }

    public void Stop()
    {
        node.PacketReceived -= OnPacketReceived;
        node.RtpPacketReceived -= OnRtpPacketReceived;
        if (!stopping.IsCancellationRequested) stopping.Cancel();
        audioPackets.Writer.TryComplete();
        lock (gate)
        {
            CancelTransmitLocked();
            if (state is IntercomState.Receiving or IntercomState.WaitingForFloor)
            {
                var stats = Statistics;
                DiagnosticLog.Write($"rx stopped sender={senderId:x8} session={sessionId:x8} udp={stats.AudioPackets} decoded={stats.DecodedFrames} played={stats.PlayedFrames} plc={stats.ConcealedFrames} gaps={stats.SequenceGaps}");
            }
            recording?.Dispose();
            recording = null;
        }
    }
}
