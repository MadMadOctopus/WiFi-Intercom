namespace IntercomCompanion.Audio;

internal sealed class JitterBuffer
{
    private const int PrebufferFrames = 4;
    private const int ReorderWindow = 4;
    private readonly object gate = new();
    private readonly Dictionary<uint, short[]> frames = [];
    private uint expectedSequence;
    private bool started;
    private short[]? lastFrame;
    private int missingPolls;

    public void Reset()
    {
        lock (gate)
        {
            frames.Clear();
            expectedSequence = 0;
            started = false;
            lastFrame = null;
            missingPolls = 0;
        }
    }

    public void Push(uint sequence, short[] pcm)
    {
        lock (gate)
        {
            if (started && sequence < expectedSequence || frames.ContainsKey(sequence)) return;
            frames[sequence] = pcm;
            if (!started && frames.Count >= PrebufferFrames)
            {
                expectedSequence = frames.Keys.Min();
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
                var nextSequence = frames.Keys.Min();
                if (nextSequence > expectedSequence)
                    expectedSequence = nextSequence;
            }
            else if (++missingPolls >= 4)
            {
                expectedSequence++;
                missingPolls = 0;
            }
            foreach (var stale in frames.Keys.Where(key => key + ReorderWindow < expectedSequence).ToArray())
                frames.Remove(stale);
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
}
