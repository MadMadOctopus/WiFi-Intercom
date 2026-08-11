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

    public void Reset()
    {
        lock (gate)
        {
            frames.Clear();
            expectedSequence = 0;
            started = false;
            lastFrame = null;
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
                pcm = frame;
                realAudio = true;
                return true;
            }

            expectedSequence++;
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
