using Concentus.Enums;
using Concentus;
using IntercomCompanion.Core;

namespace IntercomCompanion.Audio;

/// <summary>20 ms, 16 kHz Opus VoIP codec matching the ESP32-C3 firmware.</summary>
internal sealed class Opus
{
    private IOpusEncoder encoder = CreateEncoder();
    private IOpusDecoder decoder = CreateDecoder();

    public void Reset()
    {
        encoder = CreateEncoder();
        decoder = CreateDecoder();
    }

    public void ResetEncoder() => encoder = CreateEncoder();
    public void ResetDecoder() => decoder = CreateDecoder();

    public byte[] Encode(short[] pcm)
    {
        if (pcm.Length != ImaAdpcm.SamplesPerFrame) throw new ArgumentException("Expected 320 samples.", nameof(pcm));
        var encoded = new byte[Rtp.OpusMaxPayloadLength];
        var count = encoder.Encode(pcm, pcm.Length, encoded, encoded.Length);
        return encoded[..count];
    }

    public short[] Decode(byte[] payload)
    {
        var pcm = new short[ImaAdpcm.SamplesPerFrame];
        var count = decoder.Decode(payload, pcm, pcm.Length, false);
        if (count != pcm.Length) throw new InvalidOperationException($"Unexpected Opus sample count: {count}.");
        return pcm;
    }

    private static IOpusEncoder CreateEncoder()
    {
        var result = OpusCodecFactory.CreateEncoder(AudioEngine.SampleRate, 1,
            OpusApplication.OPUS_APPLICATION_VOIP, null);
        result.Bitrate = 48000;
        result.Complexity = 0; // Keeps the single-core C3 comfortably real time.
        result.UseVBR = false; // Stable 120-byte payloads at 20 ms.
        result.UseInbandFEC = false; // QoS and the jitter buffer handle the small mesh's rare losses.
        return result;
    }

    private static IOpusDecoder CreateDecoder() =>
        OpusCodecFactory.CreateDecoder(AudioEngine.SampleRate, 1, null);
}
