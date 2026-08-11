using System.Threading.Channels;
using NAudio.Wave;

namespace IntercomCompanion.Audio;

internal sealed class AudioEngine : IDisposable
{
    public const int SampleRate = 16000;
    private readonly object captureGate = new();
    private readonly byte[] captureRemainder = new byte[ImaAdpcm.SamplesPerFrame * sizeof(short)];
    private readonly Channel<short[]> capturedFrames = Channel.CreateBounded<short[]>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    private readonly WaveFormat format = new(SampleRate, 16, 1);
    private readonly BufferedWaveProvider output;
    private readonly WaveInEvent input;
    private readonly WaveOutEvent speaker;
    private int capturedBytes;
    private bool disposed;

    public AudioEngine()
    {
        output = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromMilliseconds(180),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        input = new WaveInEvent
        {
            WaveFormat = format,
            BufferMilliseconds = 20,
            NumberOfBuffers = 3,
        };
        input.DataAvailable += OnDataAvailable;
        input.RecordingStopped += (_, eventArgs) =>
        {
            if (eventArgs.Exception is not null) Diagnostic?.Invoke($"Microphone stopped: {eventArgs.Exception.Message}");
        };
        speaker = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3 };
        speaker.Init(output);
    }

    public ChannelReader<short[]> CapturedFrames => capturedFrames.Reader;
    public event Action<string>? Diagnostic;

    public void Start()
    {
        input.StartRecording();
        speaker.Play();
    }

    public void EnqueuePlayback(short[] pcm)
    {
        if (pcm.Length != ImaAdpcm.SamplesPerFrame) return;
        var bytes = new byte[pcm.Length * sizeof(short)];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        output.AddSamples(bytes, 0, bytes.Length);
    }

    public void ClearPlayback() => output.ClearBuffer();

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        // This callback only slices PCM and posts complete frames. Encoding and
        // UDP send run on a worker so a packet delay cannot starve Windows audio.
        lock (captureGate)
        {
            var offset = 0;
            while (offset < eventArgs.BytesRecorded)
            {
                var copy = Math.Min(captureRemainder.Length - capturedBytes, eventArgs.BytesRecorded - offset);
                Buffer.BlockCopy(eventArgs.Buffer, offset, captureRemainder, capturedBytes, copy);
                offset += copy;
                capturedBytes += copy;
                if (capturedBytes != captureRemainder.Length) continue;

                var frame = new short[ImaAdpcm.SamplesPerFrame];
                Buffer.BlockCopy(captureRemainder, 0, frame, 0, captureRemainder.Length);
                capturedFrames.Writer.TryWrite(frame);
                capturedBytes = 0;
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        capturedFrames.Writer.TryComplete();
        input.StopRecording();
        input.Dispose();
        speaker.Stop();
        speaker.Dispose();
    }
}
