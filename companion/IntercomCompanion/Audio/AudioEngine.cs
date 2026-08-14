using System.Threading.Channels;
using NAudio.CoreAudioApi;
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
    private readonly WasapiOut speaker;
    private int capturedBytes;
    private long queuedPlaybackFrames;
    private bool captureStarted;
    private bool disposed;
    private volatile bool localPlaybackMuted;

    public AudioEngine(string? recordingDeviceName = null, string? playbackDeviceId = null)
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
            DeviceNumber = ResolveRecordingDevice(recordingDeviceName),
        };
        input.DataAvailable += OnDataAvailable;
        input.RecordingStopped += (_, eventArgs) =>
        {
            if (eventArgs.Exception is not null) Diagnostic?.Invoke($"Microphone stopped: {eventArgs.Exception.Message}");
        };
        // Shared WASAPI hands format conversion and device clocking to the
        // Windows audio engine. The older waveOut path can be unreliable with
        // 16 kHz mono on some consumer output drivers.
        // Timed shared-mode rendering is intentionally used instead of the
        // endpoint event callback. Some consumer drivers signal one primed
        // buffer but do not continue signalling event-driven 16 kHz streams.
        using var enumerator = new MMDeviceEnumerator();
        var playbackDevice = string.IsNullOrWhiteSpace(playbackDeviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(playbackDeviceId);
        speaker = new WasapiOut(playbackDevice, AudioClientShareMode.Shared, useEventSync: false, latency: 120);
        speaker.Init(output);
        speaker.PlaybackStopped += (_, eventArgs) =>
        {
            if (eventArgs.Exception is not null) Diagnostic?.Invoke($"Speaker stopped: {eventArgs.Exception.Message}");
        };
    }

    public ChannelReader<short[]> CapturedFrames => capturedFrames.Reader;
    public event Action<string>? Diagnostic;

    public int BufferedMilliseconds => output.BufferedBytes * 1000 / format.AverageBytesPerSecond;
    public long QueuedPlaybackFrames => Interlocked.Read(ref queuedPlaybackFrames);
    public bool LocalPlaybackMuted => localPlaybackMuted;

    public static IReadOnlyList<RecordingDevice> RecordingDevices()
    {
        var devices = new List<RecordingDevice>();
        for (var index = 0; index < WaveInEvent.DeviceCount; index++)
            devices.Add(new RecordingDevice(index, WaveInEvent.GetCapabilities(index).ProductName));
        return devices;
    }

    public static IReadOnlyList<PlaybackDevice> PlaybackDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(device => new PlaybackDevice(device.ID, device.FriendlyName)).ToArray();
    }

    private static int ResolveRecordingDevice(string? requestedName)
    {
        var devices = RecordingDevices();
        if (devices.Count == 0) throw new InvalidOperationException("No Windows recording devices are available.");
        return devices.FirstOrDefault(device => string.Equals(device.Name, requestedName, StringComparison.Ordinal))?.Index
               ?? devices[0].Index;
    }

    public void StartPlayback()
    {
        speaker.Play();
    }

    public void StartCapture()
    {
        if (captureStarted) return;
        input.StartRecording();
        captureStarted = true;
    }

    public void StopCapture()
    {
        if (!captureStarted) return;
        input.StopRecording();
        captureStarted = false;
    }

    public void EnqueuePlayback(short[] pcm)
    {
        if (pcm.Length != ImaAdpcm.SamplesPerFrame) return;
        if (localPlaybackMuted) return;
        var bytes = new byte[pcm.Length * sizeof(short)];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        output.AddSamples(bytes, 0, bytes.Length);
        Interlocked.Increment(ref queuedPlaybackFrames);
    }

    public void ClearPlayback() => output.ClearBuffer();

    /// <summary>Local output only: never sends a protocol message to a device.</summary>
    public void SetLocalPlaybackMuted(bool muted)
    {
        localPlaybackMuted = muted;
        if (muted) output.ClearBuffer();
    }

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
        StopCapture();
        input.Dispose();
        speaker.Stop();
        speaker.Dispose();
    }
}

internal sealed record RecordingDevice(int Index, string Name)
{
    public override string ToString() => Name;
}

internal sealed record PlaybackDevice(string Id, string Name)
{
    public override string ToString() => Name;
}
