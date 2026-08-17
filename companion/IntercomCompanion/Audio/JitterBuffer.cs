namespace IntercomCompanion.Audio;

internal sealed class JitterBuffer
{
    // 400 ms initial playout delay absorbs short Wi-Fi delivery bursts. The
    // companion queue is bounded by session lifetime; stale frames are pruned.
    private const int PrebufferFrames = 20;
    private const int ReorderWindow = 12;
    private readonly object gate = new();
    private readonly Dictionary<ushort, short[]> frames = [];
    // Reused so the 50 packets/s playout path allocates nothing per tick.
    private readonly List<ushort> staleKeys = [];
    private ushort expectedSequence;
    private bool started;
    private short[]? lastFrame;
    private int missingPolls;

    public void Reset()
    {
        lock (gate)
        {
            frames.Clear();
            staleKeys.Clear();
            expectedSequence = 0;
            started = false;
            lastFrame = null;
            missingPolls = 0;
        }
    }

    // RTP sequence numbers are 16-bit and wrap to zero after ~21.8 minutes of
    // continuous speech, so ordering must come from the signed 16-bit
    // difference and never from plain magnitude comparison.
    private static int Distance(ushort sequence, ushort reference) =>
        (short)(ushort)(sequence - reference);

    public void Push(ushort sequence, short[] pcm)
    {
        lock (gate)
        {
            if (started && Distance(sequence, expectedSequence) < 0 || frames.ContainsKey(sequence)) return;
            frames[sequence] = pcm;
            if (!started && frames.Count >= PrebufferFrames)
            {
                expectedSequence = EarliestSequenceLocked();
                started = true;
                missingPolls = 0;
            }
        }
    }

    public bool TryPop(out short[]? pcm, out bool realAudio)
    {
        lock (gate)
        {
            pcm = null;
            realAudio = false;
            if (!started) return false;
            if (frames.Remove(expectedSequence, out var frame))
            {
                expectedSequence++;
                lastFrame = frame;
                missingPolls = 0;
                pcm = frame;
                realAudio = true;
                return true;
            }

            // A normal Windows scheduling hiccup can delay a packet by more
            // than one 20 ms playout tick. Do not advance expectedSequence on
            // the first miss: doing so made every subsequently arriving frame
            // look late, turning a one-frame delay into permanent silence.
            // If a later frame is already queued, resynchronise to it after a
            // single PLC frame. With no queued future frame, allow 60 ms for
            // the expected frame before treating it as genuinely lost.
            if (frames.Count > 0)
            {
                var nextSequence = EarliestSequenceLocked();
                if (Distance(nextSequence, expectedSequence) > 0)
                    expectedSequence = nextSequence;
            }
            else if (++missingPolls >= 4)
            {
                expectedSequence++;
                missingPolls = 0;
            }
            PruneStaleLocked();
            if (lastFrame is null)
            {
                pcm = new short[ImaAdpcm.SamplesPerFrame];
                return true;
            }

            var concealed = new short[ImaAdpcm.SamplesPerFrame];
            for (var i = 0; i < concealed.Length; i++) concealed[i] = (short)(lastFrame[i] / 2);
            lastFrame = concealed;
            pcm = concealed;
            return true;
        }
    }

    // Wrap-aware minimum over the buffered window, which is always far smaller
    // than half the 16-bit sequence space.
    private ushort EarliestSequenceLocked()
    {
        ushort earliest = 0;
        var first = true;
        foreach (var key in frames.Keys)
        {
            if (!first && Distance(key, earliest) >= 0) continue;
            earliest = key;
            first = false;
        }
        return earliest;
    }

    private void PruneStaleLocked()
    {
        staleKeys.Clear();
        foreach (var key in frames.Keys)
            if (Distance(key, expectedSequence) < -ReorderWindow) staleKeys.Add(key);
        foreach (var key in staleKeys) frames.Remove(key);
    }
}
